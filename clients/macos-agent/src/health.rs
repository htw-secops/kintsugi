//! What this Mac's own log says about it shutting down by itself, told to the server so a device that
//! is failing is noticed before somebody's meeting is lost to it.
//!
//! **Read-only, and small.** It reads the unified log's "Previous shutdown cause" line (written at every
//! boot) and the kernel panic reports macOS keeps, and sends only a one-line source and a short excerpt
//! per event, never a log. A shutdown the system or a person asked for is not an event.
//!
//! **What it cannot see.** macOS does not expose corrected or fatal hardware errors to a process the way
//! Windows (WHEA) and Linux (machine checks) do, so this reports no hardware errors: only unexpected
//! shutdowns, which is also what a failing Mac shows first. The unified log keeps a limited history
//! (days, not months), so the first report may reach back less than 30 days.
//!
//! Shutdown cause codes are Apple's and are not documented: `5` is a normal shutdown. `0` (power lost)
//! and `3` (the power button held down) and every negative code (a watchdog, a temperature or power
//! fault) are reported. Any other positive code is left alone rather than guessed at.

use std::path::Path;
use std::process::Command;

use anyhow::{Context, Result};
use serde::Serialize;
use time::format_description::well_known::Rfc3339;
use time::OffsetDateTime;

use crate::config::{self, Config};
use crate::logging;

/// How far back the first report looks. Later ones start where the last one finished.
const FIRST_LOOKBACK_SECONDS: i64 = 30 * 24 * 3600;

/// The most events of one kind in one report, newest first.
const MAX_PER_KIND: usize = 20;

const MAX_DETAIL: usize = 300;

/// Where macOS keeps the reports of kernel panics.
const PANIC_REPORT_DIR: &str = "/Library/Logs/DiagnosticReports";

#[derive(Debug, Clone, Serialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct HealthEvent {
    /// RFC 3339, UTC.
    pub occurred_utc: String,
    /// Always "UnexpectedShutdown" here: the server's own name.
    pub kind: &'static str,
    pub source: String,
    pub detail: Option<String>,
}

#[derive(Debug, Serialize)]
#[serde(rename_all = "camelCase")]
struct ReportHealthEventsRequest<'a> {
    serial_number: &'a str,
    events: &'a [HealthEvent],
}

/// Reads anything new, and tells the server. Best-effort: a failure here is logged and never fails the
/// check-in it rides on. The point in time reached is only remembered after the server accepted the
/// report, so a failed send is tried again with the same window.
pub fn report_new_events(client: &reqwest::blocking::Client, config: &Config, serial_number: &str) {
    if let Err(err) = try_report(client, config, serial_number, &config::health_state_path()) {
        logging::warn(&format!("could not report device health: {err:#}"));
    }
}

fn try_report(client: &reqwest::blocking::Client, config: &Config, serial_number: &str, state: &Path) -> Result<()> {
    let now = now_epoch();
    let since = read_state(state).unwrap_or(now - FIRST_LOOKBACK_SECONDS).max(now - FIRST_LOOKBACK_SECONDS);

    let events = collect_since(since, now);
    if !events.is_empty() {
        let request = ReportHealthEventsRequest { serial_number, events: &events };
        let response = client.post(config.health_events_url()).json(&request).send().context("the report could not be sent")?;
        if !response.status().is_success() {
            anyhow::bail!("the server rejected the report (HTTP {})", response.status());
        }
        logging::info(&format!("reported {} device health event(s)", events.len()));
    }

    write_state(state, now)
}

fn now_epoch() -> i64 {
    std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .map(|d| d.as_secs() as i64)
        .unwrap_or(0)
}

fn read_state(path: &Path) -> Option<i64> {
    std::fs::read_to_string(path).ok()?.trim().parse().ok()
}

fn write_state(path: &Path, epoch: i64) -> Result<()> {
    std::fs::write(path, epoch.to_string()).with_context(|| format!("could not write {}", path.display()))
}

/// Everything new since `since` (epoch seconds).
fn collect_since(since: i64, now: i64) -> Vec<HealthEvent> {
    let mut events = shutdown_cause_events(since, now);
    events.extend(panic_report_events(since));
    cap_newest(events)
}

fn shutdown_cause_events(since: i64, now: i64) -> Vec<HealthEvent> {
    // `log show` takes a span back from now, not a point in time. A whole hour more than needed, and the
    // lines older than `since` are dropped below.
    let hours = ((now - since).max(0) / 3600) + 2;
    let output = Command::new("/usr/bin/log")
        .args(["show", "--style", "syslog", "--last", &format!("{hours}h"), "--predicate", "eventMessage CONTAINS \"Previous shutdown cause\""])
        .env("LC_ALL", "C")
        .output();
    let Ok(output) = output else {
        return Vec::new();
    };
    if !output.status.success() {
        return Vec::new();
    }
    String::from_utf8_lossy(&output.stdout)
        .lines()
        .filter_map(classify_shutdown_line)
        .filter(|event| epoch_of(&event.occurred_utc).is_some_and(|t| t >= since))
        .collect()
}

fn panic_report_events(since: i64) -> Vec<HealthEvent> {
    let Ok(entries) = std::fs::read_dir(PANIC_REPORT_DIR) else {
        return Vec::new();
    };
    entries
        .flatten()
        .filter_map(|entry| {
            let name = entry.file_name().to_string_lossy().into_owned();
            if !is_panic_report_name(&name) {
                return None;
            }
            let modified = entry.metadata().ok()?.modified().ok()?.duration_since(std::time::UNIX_EPOCH).ok()?.as_secs() as i64;
            (modified >= since).then(|| panic_event(modified, &name))
        })
        .collect()
}

/// A kernel panic report's file name, `Kernel_2026-11-03-140400_host.panic` (or `.ips` on newer macOS),
/// as opposed to an application crash report kept in the same folder.
pub(crate) fn is_panic_report_name(name: &str) -> bool {
    let lower = name.to_lowercase();
    (lower.starts_with("kernel") || lower.starts_with("panic")) && (lower.ends_with(".panic") || lower.ends_with(".ips"))
}

pub(crate) fn panic_event(epoch: i64, file_name: &str) -> HealthEvent {
    HealthEvent {
        occurred_utc: rfc3339(epoch),
        kind: "UnexpectedShutdown",
        source: "macOS kernel panic report".to_string(),
        detail: Some(truncate(&format!("The Mac panicked and restarted ({file_name})"), MAX_DETAIL)),
    }
}

/// The shutdown cause that is worth reporting, from the number in "Previous shutdown cause: N".
pub(crate) fn is_unexpected_cause(cause: i32) -> bool {
    cause <= 0 || cause == 3
}

fn describe_cause(cause: i32) -> &'static str {
    match cause {
        0 => "power was lost",
        3 => "the power button was held down",
        -60 => "a bad master directory block",
        -62 => "a watchdog reset",
        -64 | -95 => "a temperature fault",
        -74 | -75 => "the battery",
        _ if cause < 0 => "a hardware fault",
        _ => "an unexplained cause",
    }
}

/// One `log show --style syslog` line carrying "Previous shutdown cause: N", as an event when N is one
/// worth reporting, and `None` for any other line or a normal shutdown.
pub(crate) fn classify_shutdown_line(line: &str) -> Option<HealthEvent> {
    let marker = "Previous shutdown cause:";
    let after = &line[line.find(marker)? + marker.len()..];
    let cause: i32 = after.trim().split_whitespace().next()?.parse().ok()?;
    if !is_unexpected_cause(cause) {
        return None;
    }
    let epoch = line_epoch(line)?;
    Some(HealthEvent {
        occurred_utc: rfc3339(epoch),
        kind: "UnexpectedShutdown",
        source: "macOS shutdown cause".to_string(),
        detail: Some(truncate(&format!("The Mac did not shut down normally: {} (cause code {cause})", describe_cause(cause)), MAX_DETAIL)),
    })
}

/// The time at the start of a `log show --style syslog` line: `2026-11-03 14:04:00.123456+1100`.
pub(crate) fn line_epoch(line: &str) -> Option<i64> {
    let mut parts = line.split_whitespace();
    let date = parts.next()?;
    let time = parts.next()?;
    // "14:04:00.123456+1100": the clock, then the offset from UTC.
    let sign_at = time.rfind(['+', '-'])?;
    let (clock, offset) = time.split_at(sign_at);
    let clock = clock.split('.').next()?;

    let mut d = date.split('-');
    let (year, month, day): (i64, i64, i64) = (d.next()?.parse().ok()?, d.next()?.parse().ok()?, d.next()?.parse().ok()?);
    let mut c = clock.split(':');
    let (hour, minute, second): (i64, i64, i64) = (c.next()?.parse().ok()?, c.next()?.parse().ok()?, c.next()?.parse().ok()?);

    let sign = if offset.starts_with('-') { -1 } else { 1 };
    let digits = &offset[1..];
    if digits.len() != 4 {
        return None;
    }
    let offset_seconds = sign * (digits[..2].parse::<i64>().ok()? * 3600 + digits[2..].parse::<i64>().ok()? * 60);

    Some(days_from_civil(year, month, day) * 86400 + hour * 3600 + minute * 60 + second - offset_seconds)
}

/// Days since 1970-01-01 of a calendar date (Howard Hinnant's algorithm).
fn days_from_civil(year: i64, month: i64, day: i64) -> i64 {
    let y = if month <= 2 { year - 1 } else { year };
    let era = y.div_euclid(400);
    let yoe = y - era * 400;
    let doy = (153 * (if month > 2 { month - 3 } else { month + 9 }) + 2) / 5 + day - 1;
    let doe = yoe * 365 + yoe / 4 - yoe / 100 + doy;
    era * 146097 + doe - 719468
}

fn epoch_of(rfc3339_text: &str) -> Option<i64> {
    OffsetDateTime::parse(rfc3339_text, &Rfc3339).ok().map(|t| t.unix_timestamp())
}

/// Keeps the newest [`MAX_PER_KIND`] of each kind, and drops a second report of the same moment (a panic
/// is also written down as a shutdown cause a few seconds later).
fn cap_newest(mut events: Vec<HealthEvent>) -> Vec<HealthEvent> {
    events.sort_by(|a, b| b.occurred_utc.cmp(&a.occurred_utc));
    let mut kept: Vec<HealthEvent> = Vec::new();
    for event in events {
        let near = kept.iter().any(|k| match (epoch_of(&k.occurred_utc), epoch_of(&event.occurred_utc)) {
            (Some(a), Some(b)) => (a - b).abs() <= 120,
            _ => false,
        });
        if !near && kept.len() < MAX_PER_KIND {
            kept.push(event);
        }
    }
    kept
}

fn truncate(text: &str, max: usize) -> String {
    if text.chars().count() <= max {
        return text.to_string();
    }
    let mut out: String = text.chars().take(max).collect();
    out.push('…');
    out
}

pub(crate) fn rfc3339(epoch: i64) -> String {
    OffsetDateTime::from_unix_timestamp(epoch)
        .ok()
        .and_then(|t| t.format(&Rfc3339).ok())
        .unwrap_or_else(|| "1970-01-01T00:00:00Z".to_string())
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn a_normal_shutdown_is_not_an_event() {
        assert!(classify_shutdown_line("2026-11-03 14:04:00.123456+1100 localhost kernel[0]: Previous shutdown cause: 5").is_none());
    }

    #[test]
    fn power_lost_the_button_held_and_every_negative_code_are_unexpected() {
        for cause in [0, 3, -60, -62, -95, -128] {
            assert!(is_unexpected_cause(cause), "{cause}");
        }
    }

    #[test]
    fn other_positive_codes_are_left_alone_rather_than_guessed_at() {
        for cause in [1, 2, 4, 5, 6, 7] {
            assert!(!is_unexpected_cause(cause), "{cause}");
        }
    }

    #[test]
    fn a_watchdog_reset_is_reported_with_its_time_in_utc() {
        let e = classify_shutdown_line("2026-11-03 14:04:00.123456+1100 localhost kernel[0]: Previous shutdown cause: -62").unwrap();

        assert_eq!(e.kind, "UnexpectedShutdown");
        assert_eq!(e.occurred_utc, "2026-11-03T03:04:00Z");
        assert_eq!(e.source, "macOS shutdown cause");
        assert!(e.detail.unwrap().contains("watchdog"));
    }

    #[test]
    fn a_line_without_a_cause_is_not_an_event() {
        assert!(classify_shutdown_line("2026-11-03 14:04:00.1+1100 localhost kernel[0]: something else").is_none());
        assert!(classify_shutdown_line("Previous shutdown cause: notanumber").is_none());
        assert!(classify_shutdown_line("").is_none());
    }

    #[test]
    fn the_time_of_a_line_honours_its_offset_either_way() {
        assert_eq!(line_epoch("2024-11-03 14:04:00.5+0000 host x: y"), Some(1_730_642_640));
        assert_eq!(line_epoch("2024-11-03 15:04:00.5+0100 host x: y"), Some(1_730_642_640));
        assert_eq!(line_epoch("2024-11-03 09:04:00.5-0500 host x: y"), Some(1_730_642_640));
        assert_eq!(line_epoch("2024-11-03 19:34:00.5+0530 host x: y"), Some(1_730_642_640));
    }

    #[test]
    fn a_line_with_no_readable_time_has_none() {
        assert_eq!(line_epoch("nonsense"), None);
        assert_eq!(line_epoch("2024-11-03 14:04:00.5 host x: y"), None);
        assert_eq!(line_epoch(""), None);
    }

    #[test]
    fn kernel_panic_reports_are_told_apart_from_application_crashes() {
        assert!(is_panic_report_name("Kernel_2026-11-03-140400_host.panic"));
        assert!(is_panic_report_name("panic-full-2026-11-03-140400.0003.ips"));
        assert!(is_panic_report_name("Kernel-2026-11-03-140400.ips"));
        assert!(!is_panic_report_name("Safari_2026-11-03-140400_host.ips"));
        assert!(!is_panic_report_name("Kernel_notes.txt"));
    }

    #[test]
    fn a_panic_is_an_unexpected_shutdown_named_after_its_report() {
        let e = panic_event(1_730_642_640, "Kernel_x.panic");

        assert_eq!((e.kind, e.occurred_utc.as_str()), ("UnexpectedShutdown", "2024-11-03T14:04:00Z"));
        assert!(e.detail.unwrap().contains("Kernel_x.panic"));
    }

    /// A panic is written down twice, once as a report and once as the cause of the next boot.
    #[test]
    fn the_same_shutdown_seen_twice_a_few_seconds_apart_is_one_event() {
        let events = vec![panic_event(1_730_642_640, "a.panic"), panic_event(1_730_642_700, "b")];

        assert_eq!(cap_newest(events).len(), 1);
    }

    #[test]
    fn two_shutdowns_hours_apart_are_two_events() {
        let events = vec![panic_event(1_730_642_640, "a"), panic_event(1_730_642_640 + 7200, "b")];

        assert_eq!(cap_newest(events).len(), 2);
    }

    #[test]
    fn a_burst_is_cut_to_the_newest() {
        let events: Vec<HealthEvent> = (0..50).map(|i| panic_event(1_730_000_000 + i * 600, "x")).collect();

        let kept = cap_newest(events);

        assert_eq!(kept.len(), MAX_PER_KIND);
        assert_eq!(kept[0].occurred_utc, rfc3339(1_730_000_000 + 49 * 600));
    }

    #[test]
    fn the_excerpt_is_short() {
        assert!(panic_event(1, &"x".repeat(2000)).detail.unwrap().chars().count() <= MAX_DETAIL + 1);
    }

    #[test]
    fn the_report_uses_the_names_the_server_reads() {
        let events = [panic_event(1_730_642_640, "a")];
        let json = serde_json::to_string(&ReportHealthEventsRequest { serial_number: "S1", events: &events }).unwrap();

        assert!(json.contains("\"serialNumber\":\"S1\""));
        assert!(json.contains("\"occurredUtc\":\"2024-11-03T14:04:00Z\""));
        assert!(json.contains("\"kind\":\"UnexpectedShutdown\""));
    }

    #[test]
    fn the_state_survives_a_restart_and_a_missing_one_is_none() {
        let dir = std::env::temp_dir().join(format!("kintsugi-health-test-{}", std::process::id()));
        std::fs::create_dir_all(&dir).unwrap();
        let path = dir.join("state");

        assert_eq!(read_state(&path), None);
        write_state(&path, 1_730_642_640).unwrap();
        assert_eq!(read_state(&path), Some(1_730_642_640));
        std::fs::remove_dir_all(&dir).ok();
    }
}
