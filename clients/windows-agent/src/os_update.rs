use std::process::Command;

use anyhow::{Context, Result};
use serde::Serialize;

use crate::config::Config;

/// The result of a standard OS-update check: whether one is pending, and a short description of
/// what it would bring the host to, when the check can determine that.
#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct OsUpdateStatus {
    pub available: bool,
    pub latest_version: Option<String>,
    /// `(UpdateID, RevisionNumber)` of each pending update Windows says never restarts, for the
    /// silent pass. A Defender definitions update keeps its id and moves its revision, which is why
    /// both are kept.
    pub no_restart: Vec<(String, String)>,
}

/// `Host.OperatingSystemLatestVersion` is `varchar(64)` and its command validator enforces the same
/// (see HostConfiguration / CreateHostCommandValidator) — a longer value is rejected outright, so
/// the description below is truncated here rather than failing the whole check-in.
const MAX_LATEST_VERSION_LENGTH: usize = 64;

/// Windows PowerShell (5.1), not `pwsh`: it ships with every supported version of Windows, whereas
/// PowerShell 7 has to be installed. The API server has the opposite constraint — see
/// `ScriptLanguages.Interpreter` — which is why the same script has to work under both.
pub const POWERSHELL: &str = "powershell";

/// The Windows Update Agent COM API, driven from PowerShell. This is the platform's own standard
/// way of asking what's outstanding — the direct counterpart to `softwareupdate -l` on macOS and a
/// distro's package manager on Linux.
///
/// Driven through PowerShell rather than through COM bindings in Rust on purpose: `IUpdateSearcher`
/// is a late-bound automation interface whose results are `IDispatch` collections, so calling it
/// from Rust means hand-rolling `IDispatch::Invoke` for every property read. PowerShell already
/// does exactly that, ships in the box, and leaves an invocation an administrator can paste into a
/// console to see the same answer this agent got.
const SEARCH_SCRIPT: &str = r#"
$ErrorActionPreference = 'Stop'
$session = New-Object -ComObject Microsoft.Update.Session
$searcher = $session.CreateUpdateSearcher()
# IsInstalled=0 and IsHidden=0: outstanding, and not something an administrator explicitly hid.
# Type='Software' excludes driver updates, which are not what a patching policy is about and which
# an unattended install has no business replacing.
$result = $searcher.Search("IsInstalled=0 and Type='Software' and IsHidden=0")
$updates = @($result.Updates)
Write-Output ("COUNT=" + $updates.Count)
foreach ($update in $updates) {
    Write-Output ("TITLE=" + $update.Title)
    # RebootBehavior 0 is "never reboots"; 1 always does and 2 may.
    if ($update.InstallationBehavior.RebootBehavior -eq 0) {
        Write-Output ("NORESTART=" + $update.Identity.UpdateID + " " + $update.Identity.RevisionNumber)
    }
}
"#;

/// The install half. Downloads and installs everything the same search finds — deliberately a fixed
/// script, so the queue request that triggers it (see `queue`) carries no instructions of its own
/// and can never be used to install something else. Its one variable, `$onlyIds`, is set by
/// [`install_script`] from what the service called, never from a request: `$null` installs
/// everything, and a list of update ids installs only those, and of those only the ones Windows
/// still says never restart (`RebootBehavior` 0; 1 always restarts and 2 may, so both are left out,
/// as is an update that does not say).
///
/// A failure is reported as one line per update with Windows' own error code, because the batch's
/// result code alone (4, "failed") names no cause and the server's `OperatingSystemUpdateFailure`
/// classifies by that code. And it is `Write-Output` then `exit 1`, never `Write-Error`: under
/// `-Command` an error record names the whole script as its command and prints it ahead of the
/// message, so a report used to be this script's own source with no cause left in it.
const INSTALL_SCRIPT: &str = r#"
$ErrorActionPreference = 'Stop'
function Write-UpdateResults($phase, $updates, $results) {
    for ($i = 0; $i -lt $updates.Count; $i++) {
        $r = $results.GetUpdateResult($i)
        Write-Output ("{0} result code {1}, 0x{2:X8}: {3}" -f $phase, $r.ResultCode, $r.HResult, $updates.Item($i).Title)
    }
}
$session = New-Object -ComObject Microsoft.Update.Session
$searcher = $session.CreateUpdateSearcher()
$result = $searcher.Search("IsInstalled=0 and Type='Software' and IsHidden=0")

# EULA acceptance has to happen before the download, and only for updates that ask for one — this
# runs unattended, so an unaccepted EULA would otherwise stall the whole install silently.
$toInstall = New-Object -ComObject Microsoft.Update.UpdateColl
foreach ($update in $result.Updates) {
    if ($null -ne $onlyIds -and ($onlyIds -notcontains $update.Identity.UpdateID -or $update.InstallationBehavior.RebootBehavior -ne 0)) { continue }
    if (-not $update.EulaAccepted) { $update.AcceptEula() }
    $null = $toInstall.Add($update)
}
if ($toInstall.Count -eq 0) {
    Write-Output 'nothing to install'
    exit 0
}

$downloader = $session.CreateUpdateDownloader()
$downloader.Updates = $toInstall
$downloadResult = $downloader.Download()
# ResultCode 2 = Succeeded, 3 = SucceededWithErrors (some updates applied, others didn't).
if ($downloadResult.ResultCode -ne 2 -and $downloadResult.ResultCode -ne 3) {
    Write-UpdateResults 'download' $toInstall $downloadResult
    Write-Output ("download failed with result code " + $downloadResult.ResultCode)
    exit 1
}

$installer = $session.CreateUpdateInstaller()
$installer.Updates = $toInstall
$installResult = $installer.Install()
Write-UpdateResults 'install' $toInstall $installResult
Write-Output ("installed " + $toInstall.Count + " update(s), result code " + $installResult.ResultCode)
if ($installResult.RebootRequired) { Write-Output 'a reboot is required to finish' }
if ($installResult.ResultCode -ne 2 -and $installResult.ResultCode -ne 3) {
    Write-Output ("install failed with result code " + $installResult.ResultCode)
    exit 1
}
exit 0
"#;

/// [`INSTALL_SCRIPT`] with `$onlyIds` set ahead of it. The ids are quoted into the script, which is
/// safe only because [`is_update_id`] has already held each to a GUID's characters.
fn install_script(only_ids: Option<&[&str]>) -> String {
    let only_ids = match only_ids {
        None => "$null".to_string(),
        Some(ids) => format!("@({})", ids.iter().map(|id| format!("'{id}'")).collect::<Vec<_>>().join(",")),
    };
    format!("$onlyIds = {only_ids}\n{INSTALL_SCRIPT}")
}

/// A Windows Update `UpdateID`: a GUID, so hex digits and dashes only.
fn is_update_id(id: &str) -> bool {
    id.len() == 36 && id.bytes().all(|b| b.is_ascii_hexdigit() || b == b'-')
}

/// `pub(crate)` because `system_info::read_firmware_identity` drives CIM through PowerShell too,
/// for the same reason this module does — one place naming the interpreter and its flags means the
/// 5.1-vs-`pwsh` decision above is made once rather than per caller.
pub(crate) fn run_powershell(script: &str) -> Result<std::process::Output> {
    Command::new(POWERSHELL)
        .args(["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", script])
        .output()
        .context("failed to run powershell")
}

/// First line of any script whose output the agent decodes as UTF-8. Windows PowerShell 5.1 writes
/// redirected output in the console's OEM code page, so without this accented names, non-English event
/// text and curly quotes arrive as `?` or U+FFFD. The `try` is for a host with no console to set it on;
/// the script then still runs, in whatever encoding it has.
pub(crate) const UTF8_OUTPUT: &str = "try { [Console]::OutputEncoding = [Text.UTF8Encoding]::new($false) } catch { }\n";

/// [`run_powershell`] with a deadline: the interpreter is **killed** when `budget` runs out, and the
/// error's [`std::io::ErrorKind`] is `TimedOut`. `output()` cannot do that, and a read that merely stops
/// being waited for leaves `powershell.exe` running as SYSTEM after the failure has been reported, with
/// the next request starting a second one beside it.
pub(crate) fn run_powershell_within(script: &str, budget: std::time::Duration) -> Result<std::process::Output> {
    let mut command = Command::new(POWERSHELL);
    command.args(["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", script]);
    run_within(command, budget)
}

/// Runs `command` with its output captured, polls for it to finish and kills it at the deadline. Both
/// pipes are drained on threads while it runs, so a script that prints more than a pipe holds cannot
/// stall waiting for a reader.
pub(crate) fn run_within(mut command: Command, budget: std::time::Duration) -> Result<std::process::Output> {
    use std::io::Read;
    use std::process::Stdio;
    use std::time::Instant;

    let mut child = command
        .stdin(Stdio::null())
        .stdout(Stdio::piped())
        .stderr(Stdio::piped())
        .spawn()
        .context("failed to run powershell")?;
    let mut stdout = child.stdout.take().context("no stdout pipe")?;
    let mut stderr = child.stderr.take().context("no stderr pipe")?;
    let out = std::thread::spawn(move || {
        let mut bytes = Vec::new();
        let _ = stdout.read_to_end(&mut bytes);
        bytes
    });
    let err = std::thread::spawn(move || {
        let mut bytes = Vec::new();
        let _ = stderr.read_to_end(&mut bytes);
        bytes
    });

    let deadline = Instant::now() + budget;
    let status = loop {
        if let Some(status) = child.try_wait().context("failed to wait for powershell")? {
            break status;
        }
        if Instant::now() >= deadline {
            let _ = child.kill();
            let _ = child.wait();
            // The pipes close with the process, so the readers end.
            let _ = out.join();
            let _ = err.join();
            return Err(std::io::Error::new(std::io::ErrorKind::TimedOut, "powershell did not finish in time").into());
        }
        std::thread::sleep(std::time::Duration::from_millis(50));
    };

    Ok(std::process::Output { status, stdout: out.join().unwrap_or_default(), stderr: err.join().unwrap_or_default() })
}

/// True when [`run_within`] gave up on the command at its deadline.
pub(crate) fn timed_out(error: &anyhow::Error) -> bool {
    error.downcast_ref::<std::io::Error>().is_some_and(|e| e.kind() == std::io::ErrorKind::TimedOut)
}

/// Checks whether Windows Update has anything outstanding for this host.
///
/// Only ever called from the SYSTEM service. The macOS agent runs its equivalent from the *per-user*
/// process too (`softwareupdate -l` needs no elevation there), but on Windows the tray process asks
/// the service instead, via a `Plan` request — see `queue` for why that split exists at all.
pub fn check() -> Result<OsUpdateStatus> {
    let output = run_powershell(SEARCH_SCRIPT)?;

    if !output.status.success() {
        anyhow::bail!(
            "the Windows Update search exited with {}: {}",
            output.status,
            String::from_utf8_lossy(&output.stderr).trim()
        );
    }

    let status = parse_search_output(&String::from_utf8_lossy(&output.stdout));
    crate::logging::info(&format!(
        "checked for Windows updates: available={} latest_version={:?}",
        status.available, status.latest_version
    ));
    Ok(status)
}

/// The pure text-parsing half of [`check`], split out so it can be exercised directly against
/// sample output rather than only via a real (Windows-only) subprocess call — the same split the
/// macOS agent makes around `softwareupdate -l`.
fn parse_search_output(stdout: &str) -> OsUpdateStatus {
    let count = stdout
        .lines()
        .find_map(|line| line.trim().strip_prefix("COUNT="))
        .and_then(|value| value.trim().parse::<usize>().ok())
        .unwrap_or(0);

    if count == 0 {
        return OsUpdateStatus::default();
    }

    // The first title is the most significant update in the search result. There's no single
    // "version" a set of Windows updates brings the host to the way a macOS point release has one,
    // so what's reported is the closest honest equivalent: what the administrator would see at the
    // top of the Windows Update page, plus how many others are queued behind it.
    let first_title = stdout
        .lines()
        .find_map(|line| line.trim().strip_prefix("TITLE="))
        .map(str::trim)
        .filter(|title| !title.is_empty());

    let latest_version = first_title.map(|title| {
        let description = if count > 1 { format!("{title} (+{} more)", count - 1) } else { title.to_string() };
        truncate_to_char_boundary(&description, MAX_LATEST_VERSION_LENGTH)
    });

    let no_restart = stdout
        .lines()
        .filter_map(|line| line.trim().strip_prefix("NORESTART=")?.split_once(' '))
        .filter(|(id, revision)| is_update_id(id) && !revision.is_empty() && revision.bytes().all(|b| b.is_ascii_digit()))
        .map(|(id, revision)| (id.to_string(), revision.to_string()))
        .collect();

    OsUpdateStatus { available: true, latest_version, no_restart }
}

/// Trims `value` to at most `max_bytes`, never mid-character. A Windows update title routinely runs
/// well past the column's 64 characters ("2026-08 Cumulative Update for Windows 11 Version 24H2 for
/// x64-based Systems (KB5012345)"), and a plain byte slice could land inside a multi-byte character
/// and panic.
fn truncate_to_char_boundary(value: &str, max_bytes: usize) -> String {
    if value.len() <= max_bytes {
        return value.to_string();
    }

    let mut cut = max_bytes;
    while cut > 0 && !value.is_char_boundary(cut) {
        cut -= 1;
    }
    value[..cut].trim_end().to_string()
}

/// What the install script prints when Windows says the updates it just installed need a restart to
/// finish — `$installResult.RebootRequired`. [`install`] reads it back from the output, so the text
/// here and the line in [`INSTALL_SCRIPT`] have to agree; a test checks they do.
const REBOOT_REQUIRED_MARKER: &str = "a reboot is required to finish";

/// Whether Windows is waiting for a restart right now, from the two places it records that:
/// Component Based Servicing (OS updates and features) and Windows Update itself. Read live rather
/// than remembered, so it is true for an install this agent never ran and clears by itself after
/// the restart. A key that cannot be read counts as "not pending" — the same direction as the
/// other presence checks: a missed restart notice is better than a false one.
///
/// `PendingFileRenameOperations` is deliberately not read. Defender, Office, browser and driver
/// updaters queue file replacements there all the time, so it is non-empty on most machines most
/// of the day, and the dialog states a pending restart as a fact.
pub fn reboot_pending() -> bool {
    use winreg::enums::{HKEY_LOCAL_MACHINE, KEY_READ, KEY_WOW64_64KEY};
    use winreg::RegKey;

    let hklm = RegKey::predef(HKEY_LOCAL_MACHINE);
    let key_exists = |path: &str| hklm.open_subkey_with_flags(path, KEY_READ | KEY_WOW64_64KEY).is_ok();

    key_exists(r"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending")
        || key_exists(r"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired")
}

/// What the install did, beyond "it worked".
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct InstallOutcome {
    /// Whether Windows said a restart is needed to finish. Most updates do not — a Defender
    /// definitions update never does — which is why a forced run restarts only when this is true.
    pub reboot_required: bool,
}

/// Installs everything the search finds. Runs as SYSTEM, from the service — the one privileged step
/// the tray process explicitly hands off (see `queue`).
///
/// A failure [`repair_for`] recognises gets one repair and one more install, because a damaged
/// component store or a dead update service fails the same way every cycle until something repairs
/// it, and the repair is the one the server's own Failed Updates explanation tells an administrator
/// to run by hand (`OperatingSystemUpdateFailure.cs`). Once per call, never in a loop. What was tried
/// leads the reported text ("repaired with DISM, still failed: …"), so the screen tells a repair that
/// did not help from a failure nobody tried to fix, and the codes after it still match the server's
/// explanation.
pub fn install() -> Result<InstallOutcome> {
    crate::logging::info("installing pending Windows updates");
    let failure = match run_install(None, None) {
        Ok(outcome) => return Ok(outcome),
        Err(failure) => failure,
    };
    let Some(repair) = repair_for(&format!("{failure:#}")) else {
        return Err(failure);
    };
    crate::logging::warn(&format!("the Windows update install failed; trying to {} and installing once more", repair.attempt));
    if let Err(err) = run_repair(repair) {
        // No second install: it would fail on the same unrepaired store or service.
        return Err(failure.context(format!("tried to {}, which failed ({err:#})", repair.attempt)));
    }
    run_install(None, None).map_err(|retry| retry.context(format!("{}, still failed", repair.done)))
}

/// One repair [`install`] may make before its second try.
#[derive(Debug, PartialEq, Eq)]
struct Repair {
    /// What the failure text says was tried, as "tried to …" and "trying to …".
    attempt: &'static str,
    /// What it says when the repair ran and the install still failed, as "…, still failed".
    done: &'static str,
    script: &'static str,
    budget: std::time::Duration,
}

/// `DISM /Online /Cleanup-Image /RestoreHealth`, Microsoft's documented repair for a component store an
/// update cannot be applied to: it checks the store, replaces damaged files from Windows Update and
/// leaves healthy ones alone, so running it again is harmless. Started with `Start-Process` without
/// `-NoNewWindow` or a redirect, which Windows PowerShell does through ShellExecute and so hands DISM
/// none of this PowerShell's handles. That is what lets [`run_within`] keep its deadline: a DISM holding
/// our output pipe would keep the reader threads waiting after PowerShell was killed. It also means
/// DISM is not killed with it and finishes the repair by itself, the safe direction for a servicing
/// operation; the next cycle then installs on the repaired store. Exit 3010 is success that wants a
/// restart. Errors are caught and printed for the same reason [`INSTALL_SCRIPT`] never uses
/// `Write-Error`.
const RESTORE_HEALTH: Repair = Repair {
    attempt: "repair Windows' component store with DISM",
    done: "repaired with DISM",
    script: r#"
try {
    $dism = Join-Path $env:SystemRoot 'System32\Dism.exe'
    $p = Start-Process -FilePath $dism -ArgumentList '/Online', '/Cleanup-Image', '/RestoreHealth' -WindowStyle Hidden -Wait -PassThru -ErrorAction Stop
} catch {
    Write-Output $_.Exception.Message
    exit 1
}
Write-Output ('DISM exit code 0x{0:X8}' -f $p.ExitCode)
if ($p.ExitCode -ne 0 -and $p.ExitCode -ne 3010) { exit 1 }
exit 0
"#,
    // RestoreHealth usually takes 15–30 minutes, longer on a slow link since it downloads its repair
    // files; 45 is half again the slow end. Waiting longer costs more than it buys: the service thread
    // drains no queue and checks in nowhere meanwhile, and running out costs only this install's retry,
    // as DISM carries on (above). Whatever waits on the whole install allows for it through
    // [`PROMPTED_INSTALL_WORST_CASE`].
    budget: std::time::Duration::from_secs(45 * 60),
};

/// The longest [`install`] can take, for everything that waits on it: the tray's `OS_UPDATE_TIMEOUT`
/// (patch_cycle.rs), which otherwise gives up before the result and so never asks for a forced run's
/// restart, and the self-update restart helper's `STOP_TIMEOUT` (self_update.rs), since the service
/// drains its queue, this install included, before it stops. A cumulative update can take most of an
/// hour, so 60 minutes per install, twice around [`RESTORE_HEALTH`]'s 45-minute budget, plus 15 for the
/// check-in or queue request the install may wait behind: 60 + 45 + 60 + 15 = 180 minutes. Computed
/// from the DISM budget, so the two move together. [`RESTART_SERVICES`]' 5 minutes fits inside it.
pub const PROMPTED_INSTALL_WORST_CASE: std::time::Duration =
    std::time::Duration::from_secs(60 * 60 + RESTORE_HEALTH.budget.as_secs() + 60 * 60 + 15 * 60);

/// Restarts the Windows Update service and the Windows Modules Installer, for an install whose
/// service fell over mid-way. `Restart-Service` works through the service control manager and starts
/// nothing of its own, so the deadline here kills all of it — needed, since it waits for ever on a
/// service stuck stopping.
const RESTART_SERVICES: Repair = Repair {
    attempt: "restart the Windows Update services",
    done: "restarted the Windows Update services",
    script: r#"
try {
    Restart-Service -Name wuauserv, TrustedInstaller -Force -ErrorAction Stop
} catch {
    Write-Output $_.Exception.Message
    exit 1
}
exit 0
"#,
    budget: std::time::Duration::from_secs(5 * 60),
};

/// The codes that send a failure to [`RESTORE_HEALTH`]: the markers `OperatingSystemUpdateFailure.cs`
/// (src/Kintsugi.Application/PatchFailures/) explains as "Windows' update files are damaged", lower
/// case as there, plus 0x800f0922 (CBS_E_INSTALLERS_FAILED, servicing failing part-way), which the
/// server does not classify yet. Its 0x80070bc9 is left out on purpose: that is ERROR_FAIL_REBOOT_REQUIRED,
/// which a restart clears and DISM does not, and each DISM run holds the service for up to its budget.
const DAMAGED_STORE_CODES: [&str; 4] = ["0x80073712", "0x800f0831", "0x800f081f", "0x800f0922"];

/// The codes that send a failure to [`RESTART_SERVICES`]: RPC_S_CALL_FAILED and RPC_S_SERVER_UNAVAILABLE,
/// what Windows Update's COM client reports when the service behind it died mid-call.
const SERVICE_DIED_CODES: [&str; 2] = ["0x800706be", "0x800706ba"];

/// Which repair a failed install's text calls for, if any. The install script prints codes upper case
/// (`0x{2:X8}` in [`INSTALL_SCRIPT`]), so the text is lowered to match. A damaged store wins when
/// both appear, since restarting a service does nothing for it.
fn repair_for(failure: &str) -> Option<&'static Repair> {
    let failure = failure.to_ascii_lowercase();
    let names = |codes: &[&str]| codes.iter().any(|code| failure.contains(code));
    if names(&DAMAGED_STORE_CODES) {
        Some(&RESTORE_HEALTH)
    } else if names(&SERVICE_DIED_CODES) {
        Some(&RESTART_SERVICES)
    } else {
        None
    }
}

fn run_repair(repair: &Repair) -> Result<()> {
    let output = run_powershell_within(repair.script, repair.budget)?;
    let combined = format!("{}{}", String::from_utf8_lossy(&output.stdout), String::from_utf8_lossy(&output.stderr));
    crate::logging::info(&format!("tried to {} (exit {}): {}", repair.attempt, output.status, combined.trim()));
    if !output.status.success() {
        anyhow::bail!("{}", combined.trim());
    }
    Ok(())
}

/// How long the silent pass's install may run before it is killed. The pass runs on the service
/// thread, which drains the queue and hears a stop only between steps, so a Windows Update that hangs
/// must not hold it; a definitions update takes a minute or two. [`install`] has none: somebody asked
/// for it, and a cumulative update can take most of an hour.
const SILENT_INSTALL_BUDGET: std::time::Duration = std::time::Duration::from_secs(15 * 60);

/// Installs only the updates `ids` names (from [`OsUpdateStatus::no_restart`]), and of those only
/// the ones Windows still says never restart — Defender definitions and the like — for the
/// service's silent pass (`service::install_windows_updates_without_restart`). Everything else
/// stays pending for the tray's prompt. No repair here, unlike [`install`]: a DISM run alone can
/// outlast [`SILENT_INSTALL_BUDGET`], so a failure waits for the prompted install, which repairs it.
pub fn install_without_restart(ids: &[&str]) -> Result<InstallOutcome> {
    if let Some(id) = ids.iter().find(|id| !is_update_id(id)) {
        anyhow::bail!("refusing to install '{id}': not a Windows Update id");
    }
    crate::logging::info(&format!("installing {} pending Windows update(s) that need no restart", ids.len()));
    run_install(Some(ids), Some(SILENT_INSTALL_BUDGET))
}

fn run_install(only_ids: Option<&[&str]>, budget: Option<std::time::Duration>) -> Result<InstallOutcome> {
    let script = install_script(only_ids);
    let output = match budget {
        Some(budget) => run_powershell_within(&script, budget)?,
        None => run_powershell(&script)?,
    };
    let combined = format!(
        "{}{}",
        String::from_utf8_lossy(&output.stdout),
        String::from_utf8_lossy(&output.stderr)
    );

    crate::logging::info(&format!(
        "Windows update install finished (exit {}): {}",
        output.status,
        combined.trim()
    ));

    if !output.status.success() {
        anyhow::bail!("the Windows Update install exited with {}: {}", output.status, combined.trim());
    }

    Ok(InstallOutcome { reboot_required: combined.contains(REBOOT_REQUIRED_MARKER) })
}

#[derive(Debug, Serialize)]
#[serde(rename_all = "camelCase")]
struct ReportOsPatchResultRequest<'a> {
    serial_number: &'a str,
}

/// Tells the server this host's pending Windows updates were just successfully installed, so its
/// pending-update flag and target version clear immediately rather than waiting on this host's next
/// check-in to re-derive them from a fresh search. Best-effort, the same as
/// `upgrade::report_patch_result`: the update already succeeded locally by the time this is called,
/// so a failure here is only logged, never treated as undoing the install.
pub fn report_patched(client: &reqwest::blocking::Client, config: &Config, serial_number: &str) {
    let request = ReportOsPatchResultRequest { serial_number };

    match client.post(config.os_patch_result_url()).json(&request).send() {
        Ok(response) if response.status().is_success() => {
            crate::logging::info("reported successful Windows update install to the server");
        }
        Ok(response) => {
            crate::logging::warn(&format!("server rejected OS patch-result report (HTTP {})", response.status()));
        }
        Err(err) => {
            crate::logging::warn(&format!("could not report the successful Windows update install to the server: {err:#}"));
        }
    }
}

/// The name a failed Windows update install is filed under on the admin UI's Failed Updates screen.
///
/// The same arrangement the macOS agent has: the application-failure route is already in nginx's
/// agent-certificate regex and already carries `[RequireAgentIdentity]`, and
/// `ReportPatchFailureCommandHandler` files a name that resolves to no upgrade path with a null
/// platform, which the screen shows as a failure it cannot offer an AI repair for — right for an
/// OS update, which has no script to repair. `ReportOperatingSystemPatchedCommandHandler` closes
/// the row when a success is reported, so the two have to agree on this string.
pub const OS_FAILURE_APPLICATION_NAME: &str = "Windows";

/// Starts a restart of this host in `delay_seconds`, with Windows' own on-screen countdown.
///
/// Run by the service only (`queue::RequestKind::Restart`), after an administrator's forced Windows
/// update has installed *and Windows said it needs a restart to finish*: an update that is waiting on
/// a restart is not finished, and "patched" with the old files still in use is the thing a forced run
/// exists to avoid. Not asked for after an update that does not need one — a Defender definitions
/// update is the common case, and restarting a computer for a signature file buys nothing. The delay is
/// chosen by the service, never by the request (`queue::restart_decision`): five minutes when everybody
/// logged on is at their desk, one when nobody is logged on at all — and no restart at all while anybody
/// is logged on but locked or disconnected.
///
/// `shutdown.exe /r /t` rather than a restart API, so Windows itself shows the countdown and the
/// message to whoever is signed in, with nothing for this agent to draw. The command line is fixed
/// and the message has no parameters, so nothing a request carries can change what runs. Whoever is
/// at the machine can still run `shutdown /a` to abort it, as with any Windows restart notice; this
/// is a warning, not a lock.
pub fn restart(delay_seconds: u64) -> Result<()> {
    // The last line, whatever the caller decided: `/t 0` closes every open application at once with
    // no chance to save, so nothing below the minimum reaches shutdown.exe.
    if !crate::queue::restart_delay_in_bounds(delay_seconds) {
        anyhow::bail!(
            "refusing a restart delay of {delay_seconds} second(s); it must be between {} and {}",
            crate::queue::MIN_RESTART_DELAY_SECONDS,
            crate::queue::MAX_RESTART_DELAY_SECONDS
        );
    }

    let system_root = std::env::var("SystemRoot").unwrap_or_else(|_| r"C:\Windows".to_string());
    let shutdown = std::path::Path::new(&system_root).join("System32").join("shutdown.exe");

    crate::logging::info(&format!("restarting this host in {delay_seconds} second(s) to finish the Windows update"));

    let output = Command::new(&shutdown)
        .args([
            "/r",
            "/t",
            &delay_seconds.to_string(),
            "/c",
            "Kintsugi is restarting this computer to finish installing Windows updates. Please save your work.",
        ])
        .output()
        .with_context(|| format!("could not run {}", shutdown.display()))?;

    if !output.status.success() {
        anyhow::bail!(
            "shutdown.exe exited with {}: {}{}",
            output.status,
            String::from_utf8_lossy(&output.stdout).trim(),
            String::from_utf8_lossy(&output.stderr).trim()
        );
    }

    Ok(())
}

/// Files a failed Windows update install on the Failed Updates screen, under
/// [`OS_FAILURE_APPLICATION_NAME`], with the install's captured output.
///
/// Before this existed the failure was logged on the host and nowhere else, so the Hosts screen
/// showed a machine that simply never updated, indistinguishable from one waiting for its next
/// patch cycle.
///
/// Called from the service only (`service::AgentService::install_os_updates`), which is the one
/// place `install` runs and the side holding the identity — the tray reaches it through `queue` and
/// reports nothing, so a failure is never recorded twice.
pub fn report_failed(
    client: &reqwest::blocking::Client,
    config: &Config,
    serial_number: &str,
    error: &anyhow::Error,
) {
    let installed_version = crate::system_info::operating_system().unwrap_or_else(|_| "unknown".to_string());

    crate::upgrade::report_failure(client, config, serial_number, OS_FAILURE_APPLICATION_NAME, &installed_version, None, error);
}

#[cfg(test)]
mod tests {
    /// The deadline is enforced by killing the child, not by ceasing to wait for it. `ping -n` against
    /// localhost is a command every Windows has that runs for about a second per count.
    #[test]
    fn a_command_that_outlives_its_deadline_is_killed_and_reported_as_timed_out() {
        let started = std::time::Instant::now();
        let mut command = Command::new("ping");
        command.args(["-n", "60", "127.0.0.1"]);

        let result = run_within(command, std::time::Duration::from_millis(500));

        assert!(timed_out(&result.expect_err("it should time out")));
        assert!(started.elapsed() < std::time::Duration::from_secs(20), "it waited for the command to finish");
    }

    #[test]
    fn a_command_that_finishes_gives_its_output_and_exit_status() {
        let mut command = Command::new("cmd");
        command.args(["/C", "echo hello"]);

        let output = run_within(command, std::time::Duration::from_secs(30)).expect("cmd should run");

        assert!(output.status.success());
        assert!(String::from_utf8_lossy(&output.stdout).contains("hello"));
    }

    use super::*;

    #[test]
    fn the_install_script_prints_the_line_install_reads_back() {
        assert!(INSTALL_SCRIPT.contains(REBOOT_REQUIRED_MARKER), "or a restart that Windows asked for would never be seen");
    }

    /// A misspelt `$onlyIds` would read as `$null` — install everything, restarts included — so the
    /// name set and the name read are pinned together. `$null -ne` first, because with an array on
    /// the left PowerShell's `-ne` filters the array instead of comparing it.
    #[test]
    fn the_install_script_sets_the_filter_it_reads() {
        assert!(install_script(None).starts_with("$onlyIds = $null\n"));
        let id = "0b6d3f9a-4c1e-4e2b-9a57-2f0c8d1e6b3a";
        assert!(install_script(Some(&[id, id])).starts_with(&format!("$onlyIds = @('{id}','{id}')\n")));
        assert!(INSTALL_SCRIPT.contains("$null -ne $onlyIds -and ($onlyIds -notcontains $update.Identity.UpdateID -or $update.InstallationBehavior.RebootBehavior -ne 0)"));
    }

    /// The ids end up quoted inside a script that runs as SYSTEM, so only GUID-shaped ones and
    /// numeric revisions survive the parse, and the install refuses anything else.
    #[test]
    fn parse_search_output_keeps_only_well_formed_no_restart_ids() {
        let stdout = "COUNT=3\nTITLE=Cumulative Update\nTITLE=Security Intelligence Update\nNORESTART=0b6d3f9a-4c1e-4e2b-9a57-2f0c8d1e6b3a 212\nNORESTART=x');Remove-Item C:\\ 1\nNORESTART=0b6d3f9a-4c1e-4e2b-9a57-2f0c8d1e6b3a 2x\n";

        let status = parse_search_output(stdout);

        assert_eq!(status.no_restart, vec![("0b6d3f9a-4c1e-4e2b-9a57-2f0c8d1e6b3a".to_string(), "212".to_string())]);
        assert!(install_without_restart(&["x');Remove-Item C:\\"]).is_err());
    }

    #[test]
    fn a_failed_install_reports_each_updates_error_code_and_not_the_script() {
        assert!(INSTALL_SCRIPT.contains("GetUpdateResult") && INSTALL_SCRIPT.contains("HResult"), "or the report names no cause");
        assert!(!INSTALL_SCRIPT.contains("Write-Error"), "it prints the whole script into the report");
    }

    /// The two production failures this repair was written for, as the service reports them.
    #[test]
    fn a_damaged_store_is_repaired_with_dism_and_a_dead_service_by_restarting_it() {
        let failure = |code: &str| {
            format!("the Windows Update install exited with exit code: 1: install result code 4, {code}: 2026-11 Security Update (KB5132061)\r\ninstalled 1 update(s), result code 4\r\ninstall failed with result code 4")
        };

        assert_eq!(repair_for(&failure("0x800F081F")), Some(&RESTORE_HEALTH));
        assert_eq!(repair_for(&failure("0x800706BE")), Some(&RESTART_SERVICES));
        for code in DAMAGED_STORE_CODES {
            assert_eq!(repair_for(&failure(&code.to_ascii_uppercase())), Some(&RESTORE_HEALTH), "{code}");
        }
        for code in SERVICE_DIED_CODES {
            assert_eq!(repair_for(&failure(&code.to_ascii_uppercase())), Some(&RESTART_SERVICES), "{code}");
        }
        assert_eq!(repair_for(&format!("{}\n{}", failure("0x800706BE"), failure("0x80073712"))), Some(&RESTORE_HEALTH));
    }

    #[test]
    fn a_failure_no_repair_fixes_is_left_alone() {
        assert_eq!(repair_for("install result code 4, 0x80070070: Cumulative Update"), None, "disk space");
        assert_eq!(repair_for("install result code 4, 0x80070BC9: Cumulative Update"), None, "wants a restart");
        assert_eq!(repair_for("install result code 2, 0x00000000: Cumulative Update"), None);
        assert_eq!(repair_for("failed to run powershell"), None);
    }

    /// Resetting SoftwareDistribution or catroot2, or editing the registry, is what the usual
    /// "reset Windows Update" scripts do and what this agent never does unattended.
    #[test]
    fn a_repair_only_runs_dism_or_restarts_services() {
        for repair in [&RESTORE_HEALTH, &RESTART_SERVICES] {
            let script = repair.script.to_ascii_lowercase();
            for forbidden in ["softwaredistribution", "catroot", "remove-item", "rename-item", "hklm", "registry", "reg.exe", "-itemproperty"] {
                assert!(!script.contains(forbidden), "{} mentions {forbidden}", repair.attempt);
            }
        }
        assert!(RESTORE_HEALTH.script.contains("'/Online', '/Cleanup-Image', '/RestoreHealth'"));
        assert!(!RESTORE_HEALTH.script.contains("-NoNewWindow") && !RESTORE_HEALTH.script.contains("-Redirect"), "DISM would hold the output pipe past the deadline");
    }

    /// `ReportOperatingSystemPatchedCommandHandler.WindowsFailureApplicationName` closes the rows
    /// filed under this name when a success is reported, and nothing checks the two agree.
    #[test]
    fn a_failed_install_is_filed_under_the_name_the_server_closes_on_success() {
        assert_eq!(OS_FAILURE_APPLICATION_NAME, "Windows");
    }

    #[test]
    fn parse_search_output_no_updates_available() {
        let status = parse_search_output("COUNT=0\n");

        assert_eq!(status, OsUpdateStatus::default());
        assert!(!status.available);
    }

    #[test]
    fn parse_search_output_one_update_reports_its_title() {
        let stdout = "COUNT=1\nTITLE=2026-08 Cumulative Update (KB5012345)\n";

        let status = parse_search_output(stdout);

        assert!(status.available);
        assert_eq!(status.latest_version.as_deref(), Some("2026-08 Cumulative Update (KB5012345)"));
    }

    #[test]
    fn parse_search_output_several_updates_names_the_first_and_counts_the_rest() {
        let stdout = "COUNT=3\nTITLE=Cumulative Update\nTITLE=Defender definitions\nTITLE=.NET rollup\n";

        let status = parse_search_output(stdout);

        assert_eq!(status.latest_version.as_deref(), Some("Cumulative Update (+2 more)"));
    }

    #[test]
    fn parse_search_output_truncates_a_title_to_the_columns_length() {
        // The server's own validator rejects anything longer, which would fail the entire check-in
        // — not just this one field — over a long update title.
        let long_title = "2026-08 Cumulative Update for Windows 11 Version 24H2 for x64-based Systems (KB5012345)";
        let stdout = format!("COUNT=1\nTITLE={long_title}\n");

        let status = parse_search_output(&stdout);

        let reported = status.latest_version.expect("a title should still be reported");
        assert!(reported.len() <= MAX_LATEST_VERSION_LENGTH);
        assert!(long_title.starts_with(&reported));
    }

    #[test]
    fn parse_search_output_reports_available_even_with_no_parseable_title() {
        // Available but undescribed is still available — the same shape the macOS agent's own
        // "available but no parseable version" case has.
        let status = parse_search_output("COUNT=2\n");

        assert!(status.available);
        assert_eq!(status.latest_version, None);
    }

    #[test]
    fn parse_search_output_unrecognized_text_reports_not_available() {
        // Neither a count nor a title — treated as "nothing available" rather than a false
        // positive that would prompt the user to patch nothing.
        let status = parse_search_output("Access is denied.\n");

        assert!(!status.available);
    }

    #[test]
    fn truncate_to_char_boundary_never_splits_a_character() {
        let value = "Mise à jour cumulative pour Windows 11 — version 24H2 (KB5012345)";

        for limit in 1..value.len() {
            let truncated = truncate_to_char_boundary(value, limit);
            assert!(truncated.len() <= limit);
            assert!(value.starts_with(truncated.trim_end()));
        }
    }

    #[test]
    fn truncate_to_char_boundary_leaves_a_short_value_alone() {
        assert_eq!(truncate_to_char_boundary("KB5012345", MAX_LATEST_VERSION_LENGTH), "KB5012345");
    }
}
