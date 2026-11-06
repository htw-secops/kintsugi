//! What this host's own log says about it shutting down by itself and about hardware errors, told to
//! the server so a device that is failing is noticed before somebody's meeting is lost to it.
//!
//! **Read-only, and small.** It reads the systemd journal and sends only a one-line source and a short
//! excerpt per event, never a log. A shutdown the system or a person asked for is not an event: only a
//! boot whose journal ends with no shutdown marker is one.
//!
//! **What it cannot see.** A previous boot is only there if the journal is persistent
//! (`/var/log/journal` exists, the default on Ubuntu and Pop!_OS, not on every Debian). With a volatile
//! journal there is no history of earlier boots and this reports nothing about shutdowns rather than
//! guessing. Hardware errors come from the kernel's own messages (machine-check and EDAC lines), which
//! depend on the hardware and the kernel reporting them.

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

/// The most boots read in one run, newest first, so a host with a long journal does not spend a check-in
/// reading all of it.
const MAX_BOOTS: usize = 40;

/// The most events of one kind in one report. A host throwing errors in a loop is reported as a burst,
/// newest first, not as thousands of rows.
const MAX_PER_KIND: usize = 20;

const MAX_DETAIL: usize = 300;

/// What the end of a boot's journal says when the system shut down cleanly.
const CLEAN_SHUTDOWN_MARKERS: &[&str] = &[
    "journal stopped",
    "shutting down",
    "reached target shutdown",
    "reached target final step",
    "reached target power-off",
    "reached target reboot",
    "powering off",
    "rebooting",
    "reboot: restarting system",
    "reboot: power down",
    "system is powering down",
    "system is rebooting",
];

#[derive(Debug, Clone, Serialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct HealthEvent {
    /// RFC 3339, UTC.
    pub occurred_utc: String,
    /// "UnexpectedShutdown", "HardwareErrorFatal" or "HardwareErrorCorrected": the server's own names.
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

    let events = collect_since(since);
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

/// Runs `journalctl` with a locale that cannot translate what is parsed. `None` when it is not there or
/// fails: a host without systemd reports nothing rather than failing.
fn journalctl(args: &[&str]) -> Option<String> {
    let output = Command::new("journalctl")
        .args(args)
        .arg("--no-pager")
        .arg("-q")
        .env("LC_ALL", "C")
        .env("LANG", "C")
        .output()
        .ok()?;
    output.status.success().then(|| String::from_utf8_lossy(&output.stdout).into_owned())
}

/// Everything new since `since` (epoch seconds), unexpected shutdowns and hardware errors.
fn collect_since(since: i64) -> Vec<HealthEvent> {
    let mut events = unexpected_shutdowns(since);
    events.extend(hardware_errors(since));
    events
}

fn unexpected_shutdowns(since: i64) -> Vec<HealthEvent> {
    let Some(list) = journalctl(&["--list-boots"]) else {
        return Vec::new();
    };

    let mut events = Vec::new();
    // Offsets of previous boots, newest first. The current boot (0) has not ended.
    for offset in previous_boot_offsets(&list).into_iter().take(MAX_BOOTS) {
        let offset_arg = offset.to_string();
        let Some(last) = journalctl(&["-b", &offset_arg, "-n", "1", "-o", "short-unix"]).and_then(|l| last_entry_epoch(&l)) else {
            continue;
        };
        if last < since {
            // Boots are newest first: everything after this one is older still.
            break;
        }

        let tail = journalctl(&["-b", &offset_arg, "-n", "100", "-o", "cat"]).unwrap_or_default();
        if !ended_cleanly(&tail) {
            events.push(HealthEvent {
                occurred_utc: rfc3339(last),
                kind: "UnexpectedShutdown",
                source: "systemd journal".to_string(),
                detail: Some("The previous boot ended with no shutdown in its journal".to_string()),
            });
        }
    }

    cap_newest(events)
}

fn hardware_errors(since: i64) -> Vec<HealthEvent> {
    let since_arg = format!("@{since}");
    let Some(kernel) = journalctl(&["-k", "--since", &since_arg, "-o", "short-unix"]) else {
        return Vec::new();
    };

    let events = kernel.lines().filter_map(classify_hardware_line).collect();
    cap_newest(events)
}

/// Keeps the newest [`MAX_PER_KIND`] of each kind.
fn cap_newest(mut events: Vec<HealthEvent>) -> Vec<HealthEvent> {
    events.sort_by(|a, b| b.occurred_utc.cmp(&a.occurred_utc));
    let mut counts = std::collections::HashMap::<&'static str, usize>::new();
    events.retain(|e| {
        let n = counts.entry(e.kind).or_insert(0);
        *n += 1;
        *n <= MAX_PER_KIND
    });
    events
}

/// The offsets (`-1`, `-2`, ...) of the previous boots from `journalctl --list-boots`, newest first. The
/// first token of each line is the offset; line 0 is the current boot and is left out.
pub(crate) fn previous_boot_offsets(list_boots: &str) -> Vec<i32> {
    let mut offsets: Vec<i32> = list_boots
        .lines()
        .filter_map(|line| line.split_whitespace().next()?.parse::<i32>().ok())
        .filter(|offset| *offset < 0)
        .collect();
    offsets.sort_unstable_by(|a, b| b.cmp(a));
    offsets
}

/// The epoch seconds of one `-o short-unix` line: `1730000000.123456 host unit[pid]: message`.
pub(crate) fn last_entry_epoch(line: &str) -> Option<i64> {
    let first = line.lines().map(str::trim).find(|l| !l.is_empty() && !l.starts_with("--"))?;
    first.split_whitespace().next()?.split('.').next()?.parse().ok()
}

/// Whether the end of a boot's journal carries a shutdown marker, which is how a shutdown somebody or
/// something asked for differs from the power going: the second leaves no goodbye.
pub(crate) fn ended_cleanly(tail: &str) -> bool {
    let lower = tail.to_lowercase();
    CLEAN_SHUTDOWN_MARKERS.iter().any(|marker| lower.contains(marker))
}

/// A hardware error from one kernel journal line, or `None` for any other line.
pub(crate) fn classify_hardware_line(line: &str) -> Option<HealthEvent> {
    let lower = line.to_lowercase();
    let epoch = last_entry_epoch(line)?;

    let fatal = lower.contains("machine check exception")
        || lower.contains("uncorrected")
        || lower.contains("[hardware error]") && lower.contains("fatal");
    let corrected = lower.contains("machine check events logged")
        || (lower.contains("hardware error") && lower.contains("corrected"))
        || lower.contains("edac") && (lower.contains(" ce ") || lower.contains("1 ce") || lower.contains("corrected"));

    // A line that mentions "uncorrected" also contains "corrected": the fatal reading wins.
    let (kind, source) = if fatal {
        ("HardwareErrorFatal", "kernel machine check")
    } else if corrected {
        ("HardwareErrorCorrected", if lower.contains("edac") { "kernel EDAC" } else { "kernel machine check" })
    } else {
        return None;
    };

    let message = line.splitn(2, ": ").nth(1).unwrap_or(line).trim();
    Some(HealthEvent {
        occurred_utc: rfc3339(epoch),
        kind,
        source: source.to_string(),
        detail: Some(truncate(message, MAX_DETAIL)),
    })
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

    const LIST_BOOTS: &str = "\
-2 11111111111111111111111111111111 Mon 2026-11-02 08:00:01 UTC—Mon 2026-11-02 17:00:00 UTC
-1 22222222222222222222222222222222 Tue 2026-11-03 08:10:00 UTC—Tue 2026-11-03 14:04:00 UTC
 0 33333333333333333333333333333333 Wed 2026-11-04 09:00:00 UTC—Wed 2026-11-04 10:30:00 UTC
";

    #[test]
    fn previous_boots_are_the_negative_offsets_newest_first() {
        assert_eq!(previous_boot_offsets(LIST_BOOTS), vec![-1, -2]);
    }

    #[test]
    fn the_current_boot_has_not_ended_so_it_is_never_listed() {
        assert!(!previous_boot_offsets(LIST_BOOTS).contains(&0));
    }

    #[test]
    fn a_host_with_only_the_current_boot_has_no_history_to_read() {
        assert!(previous_boot_offsets(" 0 33333333333333333333333333333333 Wed 2026-11-04 09:00:00 UTC—Wed 2026-11-04 10:30:00 UTC\n").is_empty());
    }

    #[test]
    fn garbage_lists_nothing_rather_than_failing() {
        assert!(previous_boot_offsets("no journal files were found\n").is_empty());
    }

    #[test]
    fn the_time_of_a_journal_line_is_its_first_field() {
        assert_eq!(last_entry_epoch("1730642640.123456 laptop systemd[1]: Reached target Power-Off.\n"), Some(1_730_642_640));
    }

    #[test]
    fn a_separator_line_is_skipped_when_reading_the_time() {
        assert_eq!(last_entry_epoch("-- Boot abc --\n1730642640.5 laptop kernel: x\n"), Some(1_730_642_640));
        assert_eq!(last_entry_epoch(""), None);
        assert_eq!(last_entry_epoch("not a time laptop kernel: x"), None);
    }

    #[test]
    fn a_journal_that_ends_with_a_shutdown_is_a_clean_one() {
        assert!(ended_cleanly("Stopped target Basic System.\nReached target Shutdown.\nsystemd-journald[301]: Journal stopped\n"));
        assert!(ended_cleanly("reboot: Restarting system"));
        assert!(ended_cleanly("systemd-shutdown[1]: Powering off."));
    }

    /// The power going leaves no goodbye: the last lines are whatever the machine was doing.
    #[test]
    fn a_journal_that_just_stops_is_an_unexpected_shutdown() {
        assert!(!ended_cleanly("wpa_supplicant[900]: wlp2s0: CTRL-EVENT-SIGNAL-CHANGE\nNetworkManager[800]: <info> dhcp: renewing\n"));
        assert!(!ended_cleanly(""));
    }

    #[test]
    fn an_uncorrected_machine_check_is_fatal_and_not_mistaken_for_a_corrected_one() {
        let e = classify_hardware_line("1730642640.1 laptop kernel: mce: [Hardware Error]: Uncorrected error: CPU 3 Bank 8").unwrap();

        assert_eq!(e.kind, "HardwareErrorFatal");
        assert_eq!(e.occurred_utc, "2024-11-03T14:04:00Z");
    }

    #[test]
    fn a_machine_check_exception_is_fatal() {
        assert_eq!(
            classify_hardware_line("1730642640.1 laptop kernel: mce: Machine Check Exception: 4 Bank 0: be00000000800400").unwrap().kind,
            "HardwareErrorFatal"
        );
    }

    #[test]
    fn corrected_machine_checks_and_memory_errors_are_corrected() {
        assert_eq!(
            classify_hardware_line("1730642640.1 laptop kernel: mce: [Hardware Error]: Machine check events logged").unwrap().kind,
            "HardwareErrorCorrected"
        );
        let edac = classify_hardware_line("1730642640.1 laptop kernel: EDAC MC0: 1 CE on mc#0csrow#1channel#0").unwrap();
        assert_eq!((edac.kind, edac.source.as_str()), ("HardwareErrorCorrected", "kernel EDAC"));
    }

    #[test]
    fn an_ordinary_kernel_line_is_not_a_hardware_error() {
        assert!(classify_hardware_line("1730642640.1 laptop kernel: usb 1-1: new high-speed USB device number 2").is_none());
        assert!(classify_hardware_line("").is_none());
    }

    #[test]
    fn the_excerpt_is_short() {
        let long = format!("1730642640.1 laptop kernel: mce: Uncorrected error {}", "x".repeat(2000));

        assert!(classify_hardware_line(&long).unwrap().detail.unwrap().chars().count() <= MAX_DETAIL + 1);
    }

    #[test]
    fn a_burst_of_one_kind_is_cut_to_the_newest() {
        let events: Vec<HealthEvent> = (0..50)
            .map(|i| HealthEvent { occurred_utc: rfc3339(1_730_000_000 + i), kind: "HardwareErrorCorrected", source: "x".into(), detail: None })
            .collect();

        let kept = cap_newest(events);

        assert_eq!(kept.len(), MAX_PER_KIND);
        assert_eq!(kept[0].occurred_utc, rfc3339(1_730_000_049));
    }

    #[test]
    fn the_cap_is_per_kind_so_a_flood_of_one_does_not_hide_the_other() {
        let mut events: Vec<HealthEvent> = (0..30)
            .map(|i| HealthEvent { occurred_utc: rfc3339(1_730_000_000 + i), kind: "HardwareErrorCorrected", source: "x".into(), detail: None })
            .collect();
        events.push(HealthEvent { occurred_utc: rfc3339(1_729_000_000), kind: "UnexpectedShutdown", source: "x".into(), detail: None });

        assert!(cap_newest(events).iter().any(|e| e.kind == "UnexpectedShutdown"));
    }

    #[test]
    fn the_report_uses_the_names_the_server_reads() {
        let events = [HealthEvent { occurred_utc: rfc3339(1_730_642_640), kind: "UnexpectedShutdown", source: "systemd journal".into(), detail: None }];
        let json = serde_json::to_string(&ReportHealthEventsRequest { serial_number: "S1", events: &events }).unwrap();

        assert!(json.contains("\"serialNumber\":\"S1\""));
        assert!(json.contains("\"occurredUtc\":\"2024-11-03T14:04:00Z\""));
        assert!(json.contains("\"kind\":\"UnexpectedShutdown\""));
        assert!(json.contains("\"source\":\"systemd journal\""));
    }

    /// The kinds are the server's own, spelled the way `ReportHostHealthEventsCommandValidator` allows.
    #[test]
    fn every_kind_this_agent_sends_is_one_the_server_accepts() {
        for kind in ["UnexpectedShutdown", "HardwareErrorFatal", "HardwareErrorCorrected"] {
            assert!(
                ["UnexpectedShutdown", "HardwareErrorFatal", "HardwareErrorCorrected"].contains(&kind),
                "{kind}"
            );
        }
    }

    #[test]
    fn the_state_survives_a_restart_and_a_missing_one_is_none() {
        let dir = std::env::temp_dir().join(format!("kintsugi-health-test-{}", std::process::id()));
        std::fs::create_dir_all(&dir).unwrap();
        let path = dir.join("state");

        assert_eq!(read_state(&path), None);
        write_state(&path, 1_730_642_640).unwrap();
        assert_eq!(read_state(&path), Some(1_730_642_640));
        let _ = std::fs::remove_dir_all(&dir);
    }

    #[test]
    fn times_are_written_as_rfc3339_utc() {
        assert_eq!(rfc3339(0), "1970-01-01T00:00:00Z");
        assert_eq!(rfc3339(1_730_642_640), "2024-11-03T14:04:00Z");
    }
}
