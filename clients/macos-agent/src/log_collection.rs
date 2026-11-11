//! Collecting this Mac's log when an administrator asked for it from the admin UI, and sending it to the
//! server for the web UI to show.
//!
//! **Flow.** Every check-in asks `GET /api/log-collection-requests` (one cheap call; the server consumes
//! a request as it hands it over). With nothing pending that is all. With a request, the unified log is
//! read with `/usr/bin/log show --style ndjson --last <days>d`, the kernel panic reports in
//! `/Library/Logs/DiagnosticReports` newer than that are added, and the result is sent gzipped to
//! `/api/log-collection-results`. If the log cannot be read, `/api/log-collection-failures` gets one
//! plain sentence saying why.
//!
//! **Best-effort.** Nothing here fails a check-in: every error is logged and swallowed. Reading is held
//! to a hard budget of [`READ_BUDGET`]; after it the `log` process is killed and a failure is reported.
//! A request that was claimed and then could not be answered is not retried, as the server already
//! consumed it; the administrator asks again.
//!
//! **Strictly read-only.** The only program run is `/usr/bin/log show`, and the only files read are
//! panic reports (regular files, never followed through a link, never waited on).
//!
//! **Severity.** `fault` is Critical, `error` is Error, `default` and `info` are Info. The unified log
//! has no warning level, so "Warning" is never produced. `debug` is never read. "WarningsAndErrors"
//! therefore means faults and errors only.
//!
//! **Caps.** At most [`MAX_ENTRIES`] entries (the newest) and a gzip body of at most [`MAX_GZIP_BYTES`].
//! When either drops entries, the oldest go and `truncatedBefore` says where the kept ones start.
//!
//! The persistent log keeps a limited history (days, not weeks, on most Macs), so a long period can
//! return less than was asked for. The field names of `log show --style ndjson` come from Apple's
//! format, which is not documented; a line that does not match is skipped, never guessed at.

use std::collections::VecDeque;
use std::io::{BufRead, BufReader, Write};
use std::path::Path;
use std::process::{Command, Stdio};
use std::time::{Duration, Instant};

use anyhow::{Context, Result};
use flate2::write::GzEncoder;
use flate2::Compression;
use serde::{Deserialize, Serialize};

use crate::config::Config;
use crate::health::{is_panic_report_name, line_epoch, rfc3339};
use crate::logging;

const LOG_BINARY: &str = "/usr/bin/log";
const PANIC_REPORT_DIR: &str = "/Library/Logs/DiagnosticReports";

/// How long reading the log may take, from starting `log show` to its last line.
const READ_BUDGET: Duration = Duration::from_secs(5 * 60);
const TIMEOUT_REASON: &str = "Reading the log took longer than 5 minutes.";

pub(crate) const MAX_ENTRIES: usize = 20_000;
pub(crate) const MAX_GZIP_BYTES: usize = 5 * 1024 * 1024;
const MAX_SRC_CHARS: usize = 120;
const MAX_MSG_CHARS: usize = 2000;
const MAX_REASON_CHARS: usize = 500;
const MAX_PERIOD_DAYS: i64 = 365;
/// How much of a panic report's text goes into its entry.
const PANIC_EXCERPT_CHARS: usize = 1000;

#[derive(Debug, Clone, Copy, PartialEq, Eq, Deserialize)]
pub(crate) enum Detail {
    WarningsAndErrors,
    Everything,
}

#[derive(Debug, Deserialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub(crate) struct LogCollectionRequest {
    pub id: String,
    pub period_days: i64,
    pub detail: Detail,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize)]
pub(crate) enum Severity {
    Critical,
    Error,
    #[allow(dead_code)] // the wire protocol has it; the unified log has no such level
    Warning,
    Info,
}

/// One log line, with its time as epoch seconds so entries can be ordered and cut.
#[derive(Debug, Clone, PartialEq, Eq)]
pub(crate) struct Entry {
    pub epoch: i64,
    pub sev: Severity,
    pub src: String,
    pub msg: String,
}

#[derive(Serialize)]
struct WireEntry<'a> {
    t: String,
    sev: Severity,
    src: &'a str,
    msg: &'a str,
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct WirePayload<'a> {
    entries: Vec<WireEntry<'a>>,
    truncated_before: Option<String>,
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct FailureBody<'a> {
    serial_number: &'a str,
    collection_id: &'a str,
    reason: String,
}

/// Answers a pending log request, if there is one. Never fails and never takes long when there is none.
pub fn collect_and_upload_if_requested(client: &reqwest::blocking::Client, config: &Config, serial_number: &str) {
    if let Err(err) = try_collect(client, config, serial_number) {
        logging::warn(&format!("log collection: {err:#}"));
    }
}

fn try_collect(client: &reqwest::blocking::Client, config: &Config, serial_number: &str) -> Result<()> {
    let response = client
        .get(config.log_collection_requests_url())
        .query(&[("serialNumber", serial_number)])
        .send()
        .context("could not ask for a log collection request")?;
    if !response.status().is_success() {
        anyhow::bail!("the server rejected the request check (HTTP {})", response.status());
    }
    let requests: Vec<LogCollectionRequest> = response.json().context("could not parse the request check")?;
    let Some(request) = requests.into_iter().next() else {
        return Ok(());
    };
    logging::info(&format!("log collection {} requested: {} day(s), {:?}", request.id, request.period_days, request.detail));

    match read_log(&request) {
        Ok((entries, dropped)) => {
            let (body, kept) = build_body(entries, dropped, MAX_ENTRIES, MAX_GZIP_BYTES)?;
            let response = client
                .post(config.log_collection_results_url())
                .query(&[("serialNumber", serial_number), ("collectionId", request.id.as_str())])
                .header("Content-Type", "application/gzip")
                .body(body)
                .send()
                .context("the log could not be sent")?;
            if !response.status().is_success() {
                anyhow::bail!("the server rejected the log (HTTP {})", response.status());
            }
            logging::info(&format!("log collection {} sent: {kept} entries", request.id));
            Ok(())
        }
        Err(reason) => {
            logging::warn(&format!("log collection {} failed: {reason}", request.id));
            let body = FailureBody { serial_number, collection_id: &request.id, reason: clip(&reason, MAX_REASON_CHARS) };
            let response = client.post(config.log_collection_failures_url()).json(&body).send().context("the failure could not be sent")?;
            if !response.status().is_success() {
                anyhow::bail!("the server rejected the failure report (HTTP {})", response.status());
            }
            Ok(())
        }
    }
}

/// The entries (oldest first) and whether any were already dropped on the way for being too many. The
/// error is the plain sentence for the administrator.
fn read_log(request: &LogCollectionRequest) -> std::result::Result<(Vec<Entry>, bool), String> {
    let days = request.period_days.clamp(1, MAX_PERIOD_DAYS);
    let (mut entries, dropped) = read_unified_log(days, request.detail, READ_BUDGET)?;
    entries.extend(panic_report_entries(days, now_epoch()));
    Ok((entries, dropped))
}

fn read_unified_log(days: i64, detail: Detail, budget: Duration) -> std::result::Result<(Vec<Entry>, bool), String> {
    let mut child = Command::new(LOG_BINARY)
        .args(["show", "--style", "ndjson", "--last", &format!("{days}d"), "--predicate", predicate(detail)])
        .env("LC_ALL", "C")
        .stdin(Stdio::null())
        .stdout(Stdio::piped())
        .stderr(Stdio::null())
        .spawn()
        .map_err(|e| format!("The log could not be read: {e}."))?;
    let stdout = child.stdout.take().ok_or_else(|| "The log could not be read.".to_string())?;

    // Only the newest MAX_ENTRIES are kept as the lines arrive, so a month of everything does not fill
    // this Mac's memory.
    let reader = std::thread::spawn(move || {
        let mut ring: VecDeque<Entry> = VecDeque::new();
        let mut dropped = false;
        for line in BufReader::new(stdout).split(b'\n') {
            let Ok(bytes) = line else { break };
            if let Some(entry) = parse_line(&String::from_utf8_lossy(&bytes), detail) {
                if ring.len() >= MAX_ENTRIES {
                    ring.pop_front();
                    dropped = true;
                }
                ring.push_back(entry);
            }
        }
        (ring.into_iter().collect::<Vec<_>>(), dropped)
    });

    let deadline = Instant::now() + budget;
    let status = loop {
        match child.try_wait() {
            Ok(Some(status)) => break status,
            Ok(None) if Instant::now() >= deadline => {
                let _ = child.kill();
                let _ = child.wait();
                return Err(TIMEOUT_REASON.to_string());
            }
            Ok(None) => std::thread::sleep(Duration::from_millis(200)),
            Err(e) => {
                let _ = child.kill();
                return Err(format!("The log could not be read: {e}."));
            }
        }
    };
    let result = reader.join().map_err(|_| "The log could not be read.".to_string())?;
    if !status.success() {
        return Err(match status.code() {
            Some(code) => format!("The log command failed (exit code {code})."),
            None => "The log command was stopped before it finished.".to_string(),
        });
    }
    Ok(result)
}

/// The `log show` predicate for a detail level. Debug messages are never read.
pub(crate) fn predicate(detail: Detail) -> &'static str {
    match detail {
        Detail::WarningsAndErrors => "messageType == \"error\" OR messageType == \"fault\"",
        Detail::Everything => "messageType == \"error\" OR messageType == \"fault\" OR messageType == \"default\" OR messageType == \"info\"",
    }
}

/// `fault` is Critical, `error` Error, `default` and `info` Info; anything else (debug, a level Apple
/// adds later) is not read. Case does not matter: `log show` writes them capitalised.
pub(crate) fn severity_of(message_type: &str) -> Option<Severity> {
    match message_type.to_ascii_lowercase().as_str() {
        "fault" => Some(Severity::Critical),
        "error" => Some(Severity::Error),
        "default" | "info" => Some(Severity::Info),
        _ => None,
    }
}

/// One line of `log show --style ndjson`. `None` for anything that is not a usable entry: the leading
/// "Filtering the log data using ..." line, a broken line, a missing time, message or level, or a level
/// the requested detail does not include.
pub(crate) fn parse_line(line: &str, detail: Detail) -> Option<Entry> {
    let line = line.trim();
    if !line.starts_with('{') {
        return None;
    }
    let value: serde_json::Value = serde_json::from_str(line).ok()?;
    let text = |key: &str| value.get(key).and_then(|v| v.as_str()).map(str::trim).filter(|s| !s.is_empty());

    let sev = severity_of(text("messageType")?)?;
    if detail == Detail::WarningsAndErrors && sev == Severity::Info {
        return None;
    }
    let epoch = line_epoch(text("timestamp")?)?;
    let msg = value.get("eventMessage").and_then(|v| v.as_str()).filter(|s| !s.trim().is_empty())?;

    let process = text("processImagePath").map(|p| p.rsplit('/').next().unwrap_or(p));
    let src = match (process, text("subsystem")) {
        (Some(p), Some(s)) => format!("{p} ({s})"),
        (Some(p), None) => p.to_string(),
        (None, Some(s)) => s.to_string(),
        (None, None) => "unknown".to_string(),
    };
    Some(Entry { epoch, sev, src: clip(&src, MAX_SRC_CHARS), msg: clip(msg, MAX_MSG_CHARS) })
}

/// Kernel panic reports newer than the period, one Critical entry each.
fn panic_report_entries(days: i64, now: i64) -> Vec<Entry> {
    let since = now - days * 86400;
    let Ok(dir) = std::fs::read_dir(PANIC_REPORT_DIR) else {
        return Vec::new();
    };
    dir.flatten()
        .filter_map(|entry| {
            let name = entry.file_name().to_string_lossy().into_owned();
            // A real file only, as in health.rs: `file_type` does not follow links.
            if !is_panic_report_name(&name) || !entry.file_type().is_ok_and(|t| t.is_file()) {
                return None;
            }
            let modified = entry.metadata().ok()?.modified().ok()?.duration_since(std::time::UNIX_EPOCH).ok()?.as_secs() as i64;
            if modified < since {
                return None;
            }
            let text = read_head(&entry.path()).unwrap_or_default();
            Some(panic_entry(modified, &name, &text))
        })
        .collect()
}

pub(crate) fn panic_entry(epoch: i64, file_name: &str, text: &str) -> Entry {
    let excerpt = clip(text.trim(), PANIC_EXCERPT_CHARS);
    Entry { epoch, sev: Severity::Critical, src: "panic report".to_string(), msg: clip(&format!("{file_name}\n{excerpt}"), MAX_MSG_CHARS) }
}

/// The start of a text file, never following a link and never waiting on a pipe. Text only: bytes that
/// are not UTF-8 are replaced.
fn read_head(path: &Path) -> Option<String> {
    use std::io::Read;
    use std::os::unix::fs::OpenOptionsExt;
    let mut buf = Vec::new();
    std::fs::OpenOptions::new()
        .read(true)
        .custom_flags(libc::O_NONBLOCK | libc::O_NOFOLLOW)
        .open(path)
        .ok()?
        .take(8 * 1024)
        .read_to_end(&mut buf)
        .ok()?;
    Some(String::from_utf8_lossy(&buf).into_owned())
}

/// Orders the entries, applies both caps and gzips them: the body to send and how many entries it holds.
/// `already_dropped` says older entries were discarded before this point.
pub(crate) fn build_body(mut entries: Vec<Entry>, already_dropped: bool, max_entries: usize, max_bytes: usize) -> Result<(Vec<u8>, usize)> {
    entries.sort_by_key(|e| e.epoch);
    let mut dropped = already_dropped;
    if entries.len() > max_entries {
        entries.drain(..entries.len() - max_entries);
        dropped = true;
    }
    loop {
        let body = encode(&entries, dropped)?;
        if body.len() <= max_bytes || entries.len() <= 1 {
            return Ok((body, entries.len()));
        }
        // Over the size cap: shed the oldest tenth (at least one) and try again.
        let cut = (entries.len() / 10).max(1);
        entries.drain(..cut);
        dropped = true;
    }
}

fn encode(entries: &[Entry], dropped: bool) -> Result<Vec<u8>> {
    let payload = WirePayload {
        entries: entries.iter().map(|e| WireEntry { t: rfc3339(e.epoch), sev: e.sev, src: &e.src, msg: &e.msg }).collect(),
        truncated_before: if dropped { entries.first().map(|e| rfc3339(e.epoch)) } else { None },
    };
    let json = serde_json::to_vec(&payload).context("could not serialise the log")?;
    let mut gz = GzEncoder::new(Vec::new(), Compression::default());
    gz.write_all(&json).context("could not compress the log")?;
    gz.finish().context("could not compress the log")
}

/// At most `max` characters, by characters rather than bytes so a multi-byte character is never cut.
pub(crate) fn clip(text: &str, max: usize) -> String {
    text.chars().take(max).collect()
}

fn now_epoch() -> i64 {
    std::time::SystemTime::now().duration_since(std::time::UNIX_EPOCH).map(|d| d.as_secs() as i64).unwrap_or(0)
}

#[cfg(test)]
mod tests {
    use super::*;
    use flate2::read::GzDecoder;
    use std::io::Read;

    fn entry(epoch: i64, msg: &str) -> Entry {
        Entry { epoch, sev: Severity::Error, src: "kernel".into(), msg: msg.into() }
    }

    fn decode(body: &[u8]) -> serde_json::Value {
        let mut text = String::new();
        GzDecoder::new(body).read_to_string(&mut text).unwrap();
        serde_json::from_str(&text).unwrap()
    }

    const LINE: &str = r#"{"timestamp":"2026-11-09 09:24:11.123456+1100","messageType":"Error","processImagePath":"/usr/libexec/trustd","subsystem":"com.apple.trust","eventMessage":"boom"}"#;

    #[test]
    fn parses_a_line() {
        let e = parse_line(LINE, Detail::WarningsAndErrors).unwrap();
        assert_eq!(rfc3339(e.epoch), "2026-11-08T22:24:11Z");
        assert_eq!(e.sev, Severity::Error);
        assert_eq!(e.src, "trustd (com.apple.trust)");
        assert_eq!(e.msg, "boom");
    }

    #[test]
    fn timestamp_offsets_either_way() {
        let east = LINE.to_string();
        let west = LINE.replace("09:24:11.123456+1100", "09:24:11.000000-0500");
        assert_eq!(rfc3339(parse_line(&east, Detail::Everything).unwrap().epoch), "2026-11-08T22:24:11Z");
        assert_eq!(rfc3339(parse_line(&west, Detail::Everything).unwrap().epoch), "2026-11-09T14:24:11Z");
    }

    #[test]
    fn skips_the_leading_line_and_garbage() {
        assert!(parse_line("Filtering the log data using \"messageType == error\"", Detail::Everything).is_none());
        assert!(parse_line("", Detail::Everything).is_none());
        assert!(parse_line("{ not json", Detail::Everything).is_none());
        assert!(parse_line("[1,2]", Detail::Everything).is_none());
    }

    #[test]
    fn skips_lines_missing_a_field() {
        for key in ["timestamp", "messageType", "eventMessage"] {
            let mut v: serde_json::Value = serde_json::from_str(LINE).unwrap();
            v.as_object_mut().unwrap().remove(key);
            assert!(parse_line(&v.to_string(), Detail::Everything).is_none(), "{key}");
        }
        assert!(parse_line(&LINE.replace("09:24:11.123456+1100", "garbage"), Detail::Everything).is_none());
    }

    #[test]
    fn source_is_process_and_subsystem_when_present() {
        let no_sub = r#"{"timestamp":"2026-11-09 09:24:11.1+0000","messageType":"Fault","processImagePath":"/kernel","eventMessage":"m"}"#;
        let e = parse_line(no_sub, Detail::WarningsAndErrors).unwrap();
        assert_eq!((e.src.as_str(), e.sev), ("kernel", Severity::Critical));
        let only_sub = r#"{"timestamp":"2026-11-09 09:24:11.1+0000","messageType":"Fault","subsystem":"com.x","eventMessage":"m"}"#;
        assert_eq!(parse_line(only_sub, Detail::Everything).unwrap().src, "com.x");
        let none = r#"{"timestamp":"2026-11-09 09:24:11.1+0000","messageType":"Fault","eventMessage":"m"}"#;
        assert_eq!(parse_line(none, Detail::Everything).unwrap().src, "unknown");
    }

    #[test]
    fn severity_mapping() {
        assert_eq!(severity_of("Fault"), Some(Severity::Critical));
        assert_eq!(severity_of("error"), Some(Severity::Error));
        assert_eq!(severity_of("Default"), Some(Severity::Info));
        assert_eq!(severity_of("Info"), Some(Severity::Info));
        assert_eq!(severity_of("Debug"), None);
        assert_eq!(severity_of(""), None);
    }

    #[test]
    fn detail_decides_whether_info_is_kept() {
        let info = LINE.replace("Error", "Info");
        assert!(parse_line(&info, Detail::WarningsAndErrors).is_none());
        assert_eq!(parse_line(&info, Detail::Everything).unwrap().sev, Severity::Info);
        assert!(parse_line(&LINE.replace("Error", "Debug"), Detail::Everything).is_none());
    }

    #[test]
    fn predicates() {
        assert_eq!(predicate(Detail::WarningsAndErrors), "messageType == \"error\" OR messageType == \"fault\"");
        let all = predicate(Detail::Everything);
        for t in ["error", "fault", "default", "info"] {
            assert!(all.contains(&format!("messageType == \"{t}\"")));
        }
        assert!(!all.contains("debug"));
    }

    #[test]
    fn limits_are_in_characters() {
        let long = format!(r#"{{"timestamp":"2026-11-09 09:24:11.1+0000","messageType":"Error","processImagePath":"/{}","eventMessage":"{}"}}"#, "é".repeat(300), "ü".repeat(5000));
        let e = parse_line(&long, Detail::Everything).unwrap();
        assert_eq!(e.src.chars().count(), 120);
        assert_eq!(e.msg.chars().count(), 2000);
    }

    #[test]
    fn request_shape() {
        let r: Vec<LogCollectionRequest> = serde_json::from_str(r#"[{"id":"abc","periodDays":30,"detail":"WarningsAndErrors"}]"#).unwrap();
        assert_eq!(r, vec![LogCollectionRequest { id: "abc".into(), period_days: 30, detail: Detail::WarningsAndErrors }]);
        let r: Vec<LogCollectionRequest> = serde_json::from_str(r#"[{"id":"x","periodDays":7,"detail":"Everything"}]"#).unwrap();
        assert_eq!(r[0].detail, Detail::Everything);
        assert!(serde_json::from_str::<Vec<LogCollectionRequest>>("[]").unwrap().is_empty());
    }

    #[test]
    fn failure_shape() {
        let json = serde_json::to_value(FailureBody { serial_number: "S", collection_id: "id", reason: "r".into() }).unwrap();
        assert_eq!(json, serde_json::json!({"serialNumber":"S","collectionId":"id","reason":"r"}));
    }

    #[test]
    fn result_shape_and_round_trip() {
        let (body, kept) = build_body(vec![entry(1_794_000_000, "b"), entry(1_793_000_000, "a")], false, MAX_ENTRIES, MAX_GZIP_BYTES).unwrap();
        assert_eq!(kept, 2);
        assert_eq!(&body[..2], &[0x1f, 0x8b]);
        let v = decode(&body);
        assert!(v["truncatedBefore"].is_null());
        assert_eq!(v["entries"][0], serde_json::json!({"t":"2026-10-26T07:33:20Z","sev":"Error","src":"kernel","msg":"a"}));
        assert_eq!(v["entries"][1]["msg"], "b");
    }

    #[test]
    fn entry_cap_keeps_the_newest() {
        let entries: Vec<Entry> = (0..10).map(|i| entry(1_790_000_000 + i, &format!("m{i}"))).collect();
        let (body, kept) = build_body(entries, false, 4, MAX_GZIP_BYTES).unwrap();
        assert_eq!(kept, 4);
        let v = decode(&body);
        assert_eq!(v["entries"][0]["msg"], "m6");
        assert_eq!(v["truncatedBefore"], rfc3339(1_790_000_006));
    }

    #[test]
    fn already_dropped_sets_truncated_before() {
        let (body, _) = build_body(vec![entry(1_790_000_000, "a")], true, 10, MAX_GZIP_BYTES).unwrap();
        assert_eq!(decode(&body)["truncatedBefore"], rfc3339(1_790_000_000));
    }

    #[test]
    fn size_cap_drops_the_oldest() {
        // Incompressible messages, so the gzip really is large.
        let mut x: u64 = 12345;
        let entries: Vec<Entry> = (0..400)
            .map(|i| {
                let msg: String = (0..200)
                    .map(|_| {
                        x = x.wrapping_mul(6364136223846793005).wrapping_add(1442695040888963407);
                        (b'a' + ((x >> 33) % 26) as u8) as char
                    })
                    .collect();
                entry(1_790_000_000 + i, &msg)
            })
            .collect();
        let (body, kept) = build_body(entries, false, MAX_ENTRIES, 20_000).unwrap();
        assert!(body.len() <= 20_000 && kept < 400 && kept > 0);
        let v = decode(&body);
        assert_eq!(v["entries"].as_array().unwrap().len(), kept);
        let first = v["entries"][0]["t"].as_str().unwrap().to_string();
        assert_eq!(v["truncatedBefore"], first);
        assert_eq!(v["entries"][kept - 1]["t"], rfc3339(1_790_000_399));
    }

    #[test]
    fn empty_is_valid() {
        let (body, kept) = build_body(Vec::new(), false, 10, 100_000).unwrap();
        assert_eq!(kept, 0);
        let v = decode(&body);
        assert_eq!(v["entries"].as_array().unwrap().len(), 0);
        assert!(v["truncatedBefore"].is_null());
    }

    #[test]
    fn panic_entry_shape() {
        let e = panic_entry(1_790_000_000, "Kernel_x.panic", &"p".repeat(5000));
        assert_eq!((e.sev, e.src.as_str()), (Severity::Critical, "panic report"));
        assert!(e.msg.starts_with("Kernel_x.panic\n"));
        assert_eq!(e.msg.chars().count(), "Kernel_x.panic\n".len() + 1000);
    }
}
