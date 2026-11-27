use anyhow::{bail, Result};
use serde::Serialize;

/// What this host's Windows servicing state looks like, sent on every check-in so the server can
/// tell a patched machine from one that is months behind. `operating_system` alone cannot: it only
/// carries the feature build ("26200"), and every monthly cumulative update keeps that number.
///
/// Mirrors nothing on the server yet — the field is additive and optional, so an older server
/// ignores it and an older agent never sends it. Read-only: nothing here changes a Windows Update
/// setting.
#[derive(Debug, Clone, Default, PartialEq, Eq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct UpdateState {
    /// `CurrentVersion\CurrentBuild`, e.g. `"26200"`.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub build: Option<String>,
    /// `CurrentVersion\UBR`, the update revision: the number a monthly cumulative update moves.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub ubr: Option<u32>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub display_version: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub edition_id: Option<String>,
    /// Newest installed update that Windows lists as an update (`KB5012345`). Best effort: it comes
    /// from the same list as `Get-HotFix`, which does not mark which entry is the cumulative one.
    /// `ubr` is the authoritative answer to "how patched is this host".
    #[serde(skip_serializing_if = "Option::is_none")]
    pub latest_kb: Option<String>,
    /// Install date of `latest_kb`, `YYYY-MM-DD`.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub latest_kb_installed_on: Option<String>,
    /// True when either the CBS or the Windows Update reboot key exists (`os_update::reboot_pending`).
    #[serde(skip_serializing_if = "Option::is_none")]
    pub reboot_pending: Option<bool>,
    /// The last time Windows Update itself completed a scan, as RFC 3339 UTC.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub last_scan_utc: Option<String>,
    /// `ResultCode` of this agent's own search at this check-in (2 = succeeded). See `os_update`.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub scan_result_code: Option<i32>,
    /// How many updates that search found.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub scan_updates_found: Option<u32>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub policy: Option<UpdatePolicy>,
}

/// `HKLM\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate` (and its `AU` subkey). Every field is
/// empty when the key or value is absent, which is the normal state of an unmanaged machine.
#[derive(Debug, Clone, Default, PartialEq, Eq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct UpdatePolicy {
    #[serde(rename = "wuServer", skip_serializing_if = "Option::is_none")]
    pub wu_server: Option<String>,
    #[serde(rename = "useWUServer", skip_serializing_if = "Option::is_none")]
    pub use_wu_server: Option<bool>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub defer_quality_updates: Option<bool>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub defer_quality_updates_period_days: Option<u32>,
    /// `TargetReleaseVersionInfo` (e.g. `"25H2"`), reported only while `TargetReleaseVersion` is 1.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub target_release_version: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub no_auto_update: Option<bool>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub pause_quality_updates: Option<bool>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub pause_feature_updates: Option<bool>,
}

/// Longest string value passed on, so a malformed policy value cannot bloat the check-in.
const MAX_VALUE_LENGTH: usize = 256;

/// One PowerShell pass that only reads. Prints `KEY=value` lines and leaves out anything absent, so
/// a missing key is a missing line rather than an error. Same approach as `os_update`'s search
/// script: PowerShell already speaks the registry and WMI, and an administrator can paste it into a
/// console to see what the agent saw.
const STATE_SCRIPT: &str = r#"
$ErrorActionPreference = 'SilentlyContinue'
function Show($name, $value) { if ($null -ne $value -and "$value" -ne '') { Write-Output ("$name=$value") } }
function Reg($path) { Get-ItemProperty -Path $path -ErrorAction SilentlyContinue }

$cv = Reg 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion'
Show 'BUILD' $cv.CurrentBuild
Show 'UBR' $cv.UBR
Show 'DISPLAY_VERSION' $cv.DisplayVersion
Show 'EDITION_ID' $cv.EditionID

$hotfix = Get-HotFix | Where-Object { $_.InstalledOn -and $_.Description -in 'Update', 'Security Update' } |
    Sort-Object InstalledOn -Descending | Select-Object -First 1
if ($hotfix) {
    Show 'LATEST_KB' $hotfix.HotFixID
    Show 'LATEST_KB_DATE' $hotfix.InstalledOn.ToString('yyyy-MM-dd')
}

Show 'LAST_SCAN' (Reg 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\Results\Detect').LastSuccessTime

$p = Reg 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate'
$au = Reg 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\AU'
Show 'P_WUServer' $p.WUServer
Show 'P_UseWUServer' $au.UseWUServer
Show 'P_DeferQualityUpdates' $p.DeferQualityUpdates
Show 'P_DeferQualityUpdatesPeriodInDays' $p.DeferQualityUpdatesPeriodInDays
Show 'P_TargetReleaseVersion' $p.TargetReleaseVersion
Show 'P_TargetReleaseVersionInfo' $p.TargetReleaseVersionInfo
Show 'P_NoAutoUpdate' $au.NoAutoUpdate
Show 'P_PauseQualityUpdates' $p.PauseQualityUpdates
Show 'P_PauseFeatureUpdates' $p.PauseFeatureUpdates
"#;

/// Reads the servicing state. Only the PowerShell call can fail; missing registry values never do.
/// `scan` is the result of this check-in's own update search, when it ran (see `os_update::check`).
pub fn collect(scan: Option<&crate::os_update::OsUpdateStatus>) -> Result<UpdateState> {
    let output = crate::os_update::run_powershell(STATE_SCRIPT)?;
    if !output.status.success() {
        bail!(
            "reading the Windows servicing state exited with {}: {}",
            output.status,
            String::from_utf8_lossy(&output.stderr).trim()
        );
    }

    let mut state = parse(&String::from_utf8_lossy(&output.stdout));
    // Read from the registry by `os_update`, which the confirm dialog already trusts for this.
    state.reboot_pending = Some(crate::os_update::reboot_pending());
    if let Some(scan) = scan {
        state.scan_result_code = scan.result_code;
        state.scan_updates_found = scan.updates_found;
    }
    Ok(state)
}

/// The text half of [`collect`], split out so it can be tested against sample output on any host.
fn parse(stdout: &str) -> UpdateState {
    let value = |key: &str| -> Option<&str> {
        stdout
            .lines()
            .find_map(|line| line.trim().strip_prefix(key)?.strip_prefix('='))
            .map(str::trim)
            .filter(|v| !v.is_empty())
    };
    let text = |key: &str| value(key).map(|v| v.chars().take(MAX_VALUE_LENGTH).collect::<String>());
    // Registry DWORDs come through as 0/1 (or "True"/"False" from a test-path style value).
    let flag = |key: &str| value(key).map(|v| v != "0" && !v.eq_ignore_ascii_case("false"));
    let number = |key: &str| value(key).and_then(|v| v.parse::<u32>().ok());

    let policy = UpdatePolicy {
        wu_server: text("P_WUServer"),
        use_wu_server: flag("P_UseWUServer"),
        defer_quality_updates: flag("P_DeferQualityUpdates"),
        defer_quality_updates_period_days: number("P_DeferQualityUpdatesPeriodInDays"),
        // The version string means nothing unless the policy that turns it on is set.
        target_release_version: if flag("P_TargetReleaseVersion") == Some(true) { text("P_TargetReleaseVersionInfo") } else { None },
        no_auto_update: flag("P_NoAutoUpdate"),
        pause_quality_updates: flag("P_PauseQualityUpdates"),
        pause_feature_updates: flag("P_PauseFeatureUpdates"),
    };

    UpdateState {
        build: text("BUILD"),
        ubr: number("UBR"),
        display_version: text("DISPLAY_VERSION"),
        edition_id: text("EDITION_ID"),
        latest_kb: text("LATEST_KB"),
        latest_kb_installed_on: text("LATEST_KB_DATE"),
        reboot_pending: None,
        last_scan_utc: value("LAST_SCAN").and_then(scan_time_to_rfc3339),
        scan_result_code: None,
        scan_updates_found: None,
        policy: (policy != UpdatePolicy::default()).then_some(policy),
    }
}

/// Windows writes `LastSuccessTime` as `"2026-11-20 01:02:03"` in UTC. Anything else is dropped
/// rather than guessed at.
fn scan_time_to_rfc3339(raw: &str) -> Option<String> {
    let (date, time) = raw.split_once(' ')?;
    let digits = |s: &str, parts: usize| s.split(['-', ':']).count() == parts && s.split(['-', ':']).all(|p| !p.is_empty() && p.bytes().all(|b| b.is_ascii_digit()));
    (date.len() == 10 && time.len() == 8 && digits(date, 3) && digits(time, 3)).then(|| format!("{date}T{time}Z"))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn september_build_is_reported() {
        let state = parse("BUILD=26200\nUBR=9457\nDISPLAY_VERSION=25H2\nEDITION_ID=Professional\n");

        assert_eq!(state.build.as_deref(), Some("26200"));
        assert_eq!(state.ubr, Some(9457));
        assert_eq!(state.display_version.as_deref(), Some("25H2"));
        assert_eq!(state.edition_id.as_deref(), Some("Professional"));
    }

    #[test]
    fn newer_build_is_reported_as_is() {
        let state = parse("BUILD=26300\nUBR=9974\nLATEST_KB=KB5099999\nLATEST_KB_DATE=2026-11-12\n");

        assert_eq!(state.build.as_deref(), Some("26300"));
        assert_eq!(state.ubr, Some(9974));
        assert_eq!(state.latest_kb.as_deref(), Some("KB5099999"));
        assert_eq!(state.latest_kb_installed_on.as_deref(), Some("2026-11-12"));
    }

    #[test]
    fn wsus_and_deferral_policy_is_reported() {
        let state = parse(
            "BUILD=26200\nUBR=9457\nP_WUServer=http://wsus.example.com:8530\nP_UseWUServer=1\nP_DeferQualityUpdates=1\n\
             P_DeferQualityUpdatesPeriodInDays=14\nP_TargetReleaseVersion=1\nP_TargetReleaseVersionInfo=25H2\n\
             P_NoAutoUpdate=0\nP_PauseQualityUpdates=1\n",
        );

        let policy = state.policy.expect("policy keys were present");
        assert_eq!(policy.wu_server.as_deref(), Some("http://wsus.example.com:8530"));
        assert_eq!(policy.use_wu_server, Some(true));
        assert_eq!(policy.defer_quality_updates, Some(true));
        assert_eq!(policy.defer_quality_updates_period_days, Some(14));
        assert_eq!(policy.target_release_version.as_deref(), Some("25H2"));
        assert_eq!(policy.no_auto_update, Some(false));
        assert_eq!(policy.pause_quality_updates, Some(true));
        assert_eq!(policy.pause_feature_updates, None);
    }

    #[test]
    fn target_version_text_is_ignored_while_the_policy_is_off() {
        let state = parse("P_TargetReleaseVersion=0\nP_TargetReleaseVersionInfo=23H2\nP_NoAutoUpdate=0\n");

        assert_eq!(state.policy.expect("one policy value present").target_release_version, None);
    }

    #[test]
    fn missing_keys_leave_every_field_empty() {
        assert_eq!(parse(""), UpdateState::default());
        assert_eq!(parse("Access is denied.\n"), UpdateState::default());
    }

    #[test]
    fn empty_state_serializes_to_an_empty_object() {
        // Additive on the wire: nothing but the fields that were read.
        assert_eq!(serde_json::to_string(&UpdateState::default()).unwrap(), "{}");
        assert_eq!(
            serde_json::to_string(&parse("BUILD=26200\nUBR=9457\nP_WUServer=http://w\nP_UseWUServer=1\n")).unwrap(),
            r#"{"build":"26200","ubr":9457,"policy":{"wuServer":"http://w","useWUServer":true}}"#
        );
    }

    #[test]
    fn last_scan_time_is_converted_or_dropped() {
        assert_eq!(parse("LAST_SCAN=2026-11-20 01:02:03\n").last_scan_utc.as_deref(), Some("2026-11-20T01:02:03Z"));
        assert_eq!(parse("LAST_SCAN=yesterday\n").last_scan_utc, None);
    }

    #[test]
    fn overlong_values_are_cut() {
        let state = parse(&format!("P_WUServer={}\n", "a".repeat(1000)));

        assert_eq!(state.policy.unwrap().wu_server.unwrap().len(), MAX_VALUE_LENGTH);
    }
}
