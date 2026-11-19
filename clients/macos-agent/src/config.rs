use std::ffi::CStr;
use std::path::{Path, PathBuf};

use anyhow::{Context, Result};
use serde::Deserialize;

/// Default backend base URL, used when no config file or environment
/// override is present. This will change once the backend has a stable,
/// non-development address.
const DEFAULT_API_BASE_URL: &str = "https://kintsugi.example.com:8443";

const CONFIG_PATH: &str = "/Library/Application Support/kintsugi-agent/config.toml";

/// The part of `config.toml` that is not a secret — `api_base_url` alone — written by the root
/// daemon on every run (`write_server_file`) and by packaging/install.sh, `0644`.
///
/// It exists for the per-user process on a Mac whose user is not an administrator. That process
/// needs the server's address and nothing else from `config.toml`, but `config.toml` is
/// `root:admin 0640` because it carries the enrollment token, and a standard account is not in
/// `admin`. Without this file such a Mac's per-user process fell back to `DEFAULT_API_BASE_URL`,
/// the `kintsugi.example.com` placeholder, and never reached the server at all. Copying the address
/// out is what lets the token stay administrator-only while every account can find the server.
const SERVER_FILE_PATH: &str = "/Library/Application Support/kintsugi-agent/server.toml";

/// How long the per-user process waits at start for either file above to become readable — see
/// `Config::load_for_user_agent`.
const SERVER_FILE_WAIT: std::time::Duration = std::time::Duration::from_secs(60);
const ENV_OVERRIDE: &str = "PATCHING_AGENT_API_BASE_URL";
const ENROLLMENT_TOKEN_ENV_OVERRIDE: &str = "PATCHING_AGENT_ENROLLMENT_TOKEN";

/// Where this agent's mutual-TLS identity (certificate, private key, pinned CA and
/// artifact-signing public key — see `identity`) is persisted once enrolled. Shared between the
/// root LaunchDaemon (which performs enrollment) and the per-user `--agent` process (which only
/// ever reads it) — see `identity::restrict_key_permissions` and packaging/install.sh.
const IDENTITY_DIR: &str = "/Library/Application Support/kintsugi-agent/identity";

/// Shared handoff directory between the root LaunchDaemon and the per-user LaunchAgent
/// (`--agent` mode) for the privileged steps the UI agent can't do itself — see `queue`. A
/// drop-box, `root:staff 1730` (`queue::DROP_BOX_MODE`): any local account may leave a request,
/// nobody but root may list one, and only root ever acts on one.
const QUEUE_DIR: &str = "/Library/Application Support/kintsugi-agent/queue";

/// The per-user process's drop-box for "open a root shell for this session id" — deliberately a
/// *second* queue rather than another `RequestKind` in the first one.
///
/// The main queue is drained by the check-in daemon, in the same invocation that registers this
/// host and runs its patches, and launchd will not run two instances of one job. A shell session
/// lasts as long as somebody is typing in it, so putting it there would stall this host's check-ins
/// for the length of a support call. Its own directory, watched by its own LaunchDaemon
/// (`REMOTE_SHELL_LAUNCHD_LABEL`), is what keeps the two independent.
const REMOTE_SHELL_QUEUE_DIR: &str = "/Library/Application Support/kintsugi-agent/remote-shell";

/// What the daemon's last pre-fetch of the pending macOS updates left staged, and when.
///
/// Written by root and read by the per-user process — hence this directory rather than the
/// drop-box queue beside it, and hence 0644. There is no OS-level way to ask macOS which
/// updates are staged (`softwareupdate -l` lists one until it is *installed*, and
/// `/var/db/softwareupdate/journal.plist` records only what already installed), so the agent has to
/// remember what it fetched. See `os_update::StagedDownloads`.
const OS_DOWNLOAD_STATE_PATH: &str = "/Library/Application Support/kintsugi-agent/os-download-state.json";

/// Where the daemon publishes how far its OS-update pre-fetch has got, for the per-user process to
/// draw the menu bar's progress bar from. Root writes, the user reads, so 0644 like the state file
/// beside it — see `os_update::DownloadProgress`.
const OS_DOWNLOAD_PROGRESS_PATH: &str = "/Library/Application Support/kintsugi-agent/os-download-progress.json";

/// The daemon's note that a macOS install is under way whose reboot it will not survive — written
/// before `softwareupdate -i -a -R` runs and judged on the next invocation. Root writes and root
/// reads; it sits beside the two files above for no better reason than being about the same update.
/// See `os_update::PendingInstall`.
const OS_INSTALL_PENDING_PATH: &str = "/Library/Application Support/kintsugi-agent/os-install-pending.json";

/// The root daemon's own durable action log — a guaranteed location regardless of how the
/// process was invoked (launchd's `StandardOutPath` redirect on top of this is redundant but
/// harmless). See `logging`.
const DAEMON_LOG_PATH: &str = "/Library/Application Support/kintsugi-agent/daemon.log";

/// This host's assigned check-in minute-of-hour (0-59), persisted so it survives across
/// invocations — see `checkin_schedule`. Deliberately separate from `CONFIG_PATH`: that file is
/// always overwritten wholesale on every install/reinstall (see packaging/install.sh), whereas
/// this needs to survive one.
/// Where the time of the last health report is kept, so the next one reads only what is new.
const HEALTH_STATE_PATH: &str = "/Library/Application Support/kintsugi-agent/health-reported";

const CHECKIN_SCHEDULE_PATH: &str = "/Library/Application Support/kintsugi-agent/checkin-schedule.json";
/// The self-service offers the root daemon writes for the menu bar's "Install Software" submenu —
/// see `self_service`. Beside `checkin-schedule.json`, and readable by the per-user process for the
/// same reason.
const SELF_SERVICE_PATH: &str = "/Library/Application Support/kintsugi-agent/self-service.json";

/// Where the root LaunchDaemon's own job definition lives — `checkin_schedule` rewrites this file
/// in place (and reloads it with launchd) whenever this host's assigned check-in minute changes,
/// the same way `self_update` replaces `INSTALLED_BINARY_PATH` in place.
const DAEMON_PLIST_PATH: &str = "/Library/LaunchDaemons/au.com.sharpblue.kintsugiagent.plist";

/// Where the per-user LaunchAgent's job definition lives (see packaging/install.sh) — the
/// counterpart to `DAEMON_PLIST_PATH`, torn down alongside it by `self_removal` when a removal is
/// confirmed.
const UI_PLIST_PATH: &str = "/Library/LaunchAgents/au.com.sharpblue.kintsugiagent-ui.plist";

/// Where the remote-shell LaunchDaemon's job definition lives (see packaging/install.sh).
///
/// `self_update` writes this one **if and only if it is absent**, which is not tidiness: a Mac
/// self-updating from a release that predates remote shells gets the new binary and no job to run
/// it under, and would report every shell session as never connecting with nothing to explain why —
/// the same gap the Linux agent's `restart_remote_control_unit` closes, and found there first.
const REMOTE_SHELL_PLIST_PATH: &str =
    "/Library/LaunchDaemons/au.com.sharpblue.kintsugiagent-remote-shell.plist";

/// Where the root daemon installs itself (see packaging/install.sh) — also this agent's own
/// self-update target: `self_update::check_and_apply` replaces exactly this path.
const INSTALLED_BINARY_PATH: &str = "/usr/local/bin/kintsugi-agent";

/// The agent's own copy of `mas` (mas-cli), installed beside the binary by packaging/install.sh and
/// replaced by `self_update` from the same archive — the only thing that runs an App Store update,
/// and the reason the archive carries it. The server's `AppStoreUpgradeScript` names this exact path
/// and refuses it unless root owns it: that script runs as root from the daemon, and a root daemon
/// executing Homebrew's `/opt/homebrew/bin/mas` would be root for whoever owns the Homebrew prefix.
/// `kintsugi-` prefixed so it can never be mistaken for, or shadowed by, a user's own `mas` on PATH.
pub const MAS_BINARY_NAME: &str = "kintsugi-mas";
const MAS_BINARY_PATH: &str = "/usr/local/bin/kintsugi-mas";

/// launchd labels for the two jobs packaging/install.sh installs — kept here (rather than only as
/// bash variables in that script) so `self_update` can `launchctl kickstart` both of them by name
/// once it's replaced the binary they both run.
pub const DAEMON_LAUNCHD_LABEL: &str = "au.com.sharpblue.kintsugiagent";
pub const UI_LAUNCHD_LABEL: &str = "au.com.sharpblue.kintsugiagent-ui";

/// The third job: the root LaunchDaemon that runs one remote shell session and exits, started by
/// `WatchPaths` on `REMOTE_SHELL_QUEUE_DIR`. See that constant for why it is not the first daemon.
pub const REMOTE_SHELL_LAUNCHD_LABEL: &str = "au.com.sharpblue.kintsugiagent-remote-shell";

#[derive(Debug, Deserialize, Default)]
struct FileConfig {
    api_base_url: Option<String>,
    enrollment_token: Option<String>,
}

#[derive(Debug, Clone)]
pub struct Config {
    pub api_base_url: String,
    /// The one-time shared secret this agent presents to enroll (see `identity::enroll`). Only
    /// needed until enrollment succeeds; safe to remove from config.toml afterward, though leaving
    /// it is harmless since it's never used again once an identity exists on disk.
    pub enrollment_token: Option<String>,
}

impl Config {
    /// Resolves configuration in priority order: environment variable,
    /// then config file, then the built-in default. Each field is resolved
    /// independently, so an environment override for one doesn't suppress
    /// the file for the other.
    ///
    /// The file is `config.toml` when this process can read it — root always can, and so can an
    /// administrator — and otherwise `server.toml`, which carries the address and no token (see
    /// `SERVER_FILE_PATH`). The second is all a standard user's per-user process ever gets.
    pub fn load() -> Self {
        Self::load_from(&[Path::new(CONFIG_PATH), Path::new(SERVER_FILE_PATH)])
    }

    /// `load`, for the per-user process: waits up to `SERVER_FILE_WAIT` for one of the two files
    /// to become readable before settling for the placeholder default.
    ///
    /// A self-update restarts this process a moment before it restarts the daemon (see
    /// `self_update::restart_launchd_jobs`), so on a standard user's Mac the first update to this
    /// release starts it before anything has written `server.toml`. The address is read once, at
    /// start, so settling for the placeholder there would leave the menu bar talking to
    /// `kintsugi.example.com` until the next login. The daemon writes the file within a second of
    /// starting, so the wait is normally that long; a Mac whose daemon never runs costs a minute.
    pub fn load_for_user_agent() -> Self {
        let started = std::time::Instant::now();
        while read_file_config(Path::new(CONFIG_PATH)).is_none()
            && read_file_config(Path::new(SERVER_FILE_PATH)).is_none()
            && started.elapsed() < SERVER_FILE_WAIT
        {
            std::thread::sleep(std::time::Duration::from_secs(1));
        }
        Self::load()
    }

    fn load_from(config_paths: &[&Path]) -> Self {
        let file_config = config_paths.iter().find_map(|path| read_file_config(path)).unwrap_or_default();

        let api_base_url = std::env::var(ENV_OVERRIDE)
            .ok()
            .filter(|v| !v.trim().is_empty())
            .or(file_config.api_base_url)
            .unwrap_or_else(|| DEFAULT_API_BASE_URL.to_string());

        let enrollment_token = std::env::var(ENROLLMENT_TOKEN_ENV_OVERRIDE)
            .ok()
            .filter(|v| !v.trim().is_empty())
            // The packaged config.toml ships with an empty placeholder (`enrollment_token = ""`)
            // for whoever builds the installer to fill in — filtered here the same as the env
            // override above, so a still-blank placeholder is treated as "not configured" and
            // fails with a clear error, rather than silently POSTing an empty token to the server
            // and getting back a generic, hard-to-diagnose 400.
            .or(file_config.enrollment_token.filter(|v| !v.trim().is_empty()));

        Self { api_base_url, enrollment_token }
    }

    pub fn enroll_url(&self) -> String {
        format!("{}/api/host/enroll", self.api_base_url.trim_end_matches('/'))
    }

    pub fn register_host_url(&self) -> String {
        format!("{}/api/host", self.api_base_url.trim_end_matches('/'))
    }

    /// The remote control socket's address.
    ///
    /// One path for both sockets — the standing control one (no `session_id`) and a session's media
    /// one (with) — because nginx gates this route with an exact match on a single path segment and
    /// tells the two apart by query string. See nginx/default.conf and RemoteControlController.
    ///
    /// The scheme is rewritten because `api_base_url` is an HTTP address and tungstenite will not
    /// accept one: it requires `ws`/`wss` and refuses the request outright rather than assuming.
    pub fn remote_control_url(&self, serial_number: &str, session_id: Option<&str>) -> String {
        let base = self.api_base_url.trim_end_matches('/');
        let base = match base.split_once("://") {
            Some(("https", rest)) => format!("wss://{rest}"),
            Some(("http", rest)) => format!("ws://{rest}"),
            // Already a socket scheme, or something unrecognised — passed through so a
            // misconfiguration fails at connect time with the address in the message, rather than
            // being silently rewritten into a different one.
            _ => base.to_string(),
        };

        match session_id {
            Some(session_id) => format!("{base}/api/remote-control?serialNumber={serial_number}&sessionId={session_id}"),
            None => format!("{base}/api/remote-control?serialNumber={serial_number}"),
        }
    }

    pub fn register_applications_url(&self) -> String {
        format!("{}/api/applications", self.api_base_url.trim_end_matches('/'))
    }

    /// Base URL only — the caller adds `?serialNumber=` via the HTTP client's own query-building
    /// (`RequestBuilder::query`) rather than manual string formatting, so the serial number gets
    /// properly percent-encoded.
    pub fn upgrade_paths_url(&self) -> String {
        format!("{}/api/upgrade-paths", self.api_base_url.trim_end_matches('/'))
    }

    /// The fleet-wide patching schedule (how often, delay length, max delays) — see
    /// Kintsugi.WebApi/Controllers/PatchingPolicyController.cs.
    pub fn patching_policy_url(&self) -> String {
        format!("{}/api/patching-policy", self.api_base_url.trim_end_matches('/'))
    }

    /// The "patch this application now" instructions an administrator has raised against this host
    /// — see `forced_patch_run::collect` and
    /// Kintsugi.WebApi/Controllers/ForcedPatchRunsController.cs. Base URL only, for the same reason
    /// `upgrade_paths_url` is: the caller adds `?serialNumber=` through the client's own
    /// query-building so it is percent-encoded.
    pub fn forced_patch_runs_url(&self) -> String {
        format!("{}/api/forced-patch-runs", self.api_base_url.trim_end_matches('/'))
    }

    /// The latest published kintsugi-agent build for one platform — see
    /// Kintsugi.WebApi/Controllers/AgentPackagesController.cs and `self_update`.
    pub fn agent_package_latest_url(&self, platform: &str) -> String {
        format!("{}/api/agent-packages/{platform}/latest", self.api_base_url.trim_end_matches('/'))
    }

    /// Downloads the latest published package file for one platform — see `self_update`.
    pub fn agent_package_download_url(&self, platform: &str) -> String {
        format!("{}/api/agent-packages/{platform}/download", self.api_base_url.trim_end_matches('/'))
    }

    /// Tells the server an application was just successfully patched — see
    /// `upgrade::report_patch_result` and Kintsugi.WebApi/Controllers/ApplicationsController.cs.
    pub fn patch_result_url(&self) -> String {
        format!("{}/api/patch-results", self.api_base_url.trim_end_matches('/'))
    }

    /// Tells the server an upgrade script ran on this host and failed — see
    /// `upgrade::report_patch_failure` and Kintsugi.WebApi/Controllers/ApplicationsController.cs.
    /// The counterpart to `patch_result_url`, and the same in all three agents.
    pub fn patch_failure_url(&self) -> String {
        format!("{}/api/patch-failures", self.api_base_url.trim_end_matches('/'))
    }

    /// Tells the server a pending macOS update was just successfully installed — see
    /// `os_update::report_patched` and Kintsugi.WebApi/Controllers/HostsController.cs.
    pub fn os_patch_result_url(&self) -> String {
        format!("{}/api/os-patch-results", self.api_base_url.trim_end_matches('/'))
    }

    /// Tells the server what this Mac's own log says about it shutting down by itself — see
    /// `health::report_new_events` and Kintsugi.WebApi/Controllers/HostHealthController.cs.
    pub fn health_events_url(&self) -> String {
        format!("{}/api/host-health-events", self.api_base_url.trim_end_matches('/'))
    }

    /// Asks for a pending log collection request — see `log_collection::collect_and_upload_if_requested`
    /// and the server's log collection controller.
    pub fn log_collection_requests_url(&self) -> String {
        format!("{}/api/log-collection-requests", self.api_base_url.trim_end_matches('/'))
    }

    /// Sends the collected log (gzipped JSON) for a request.
    pub fn log_collection_results_url(&self) -> String {
        format!("{}/api/log-collection-results", self.api_base_url.trim_end_matches('/'))
    }

    /// Says a requested log could not be read.
    pub fn log_collection_failures_url(&self) -> String {
        format!("{}/api/log-collection-failures", self.api_base_url.trim_end_matches('/'))
    }

    /// Tells the server this host finished uninstalling itself completely, after a check-in
    /// response marked it for removal — see `self_removal::run` and
    /// Kintsugi.WebApi/Controllers/HostsController.cs.
    pub fn host_removed_url(&self) -> String {
        format!("{}/api/host-removed", self.api_base_url.trim_end_matches('/'))
    }
}

pub fn default_config_path() -> PathBuf {
    PathBuf::from(CONFIG_PATH)
}

/// The root daemon / UI agent handoff directory for privileged OS-update installs — see
/// `os_update`. A plain constant rather than something `Config` resolves, since (unlike
/// `api_base_url`) it isn't meant to be end-user-configurable.
pub fn queue_dir() -> PathBuf {
    PathBuf::from(QUEUE_DIR)
}

pub fn identity_dir() -> PathBuf {
    PathBuf::from(IDENTITY_DIR)
}

pub fn daemon_log_path() -> PathBuf {
    PathBuf::from(DAEMON_LOG_PATH)
}

pub fn os_download_state_path() -> PathBuf {
    PathBuf::from(OS_DOWNLOAD_STATE_PATH)
}

pub fn os_install_pending_path() -> PathBuf {
    PathBuf::from(OS_INSTALL_PENDING_PATH)
}

pub fn os_download_progress_path() -> PathBuf {
    PathBuf::from(OS_DOWNLOAD_PROGRESS_PATH)
}

/// Where the time of the last health report is kept.
pub fn health_state_path() -> PathBuf {
    PathBuf::from(HEALTH_STATE_PATH)
}

pub fn checkin_schedule_path() -> PathBuf {
    PathBuf::from(CHECKIN_SCHEDULE_PATH)
}

pub fn self_service_path() -> PathBuf {
    PathBuf::from(SELF_SERVICE_PATH)
}

pub fn daemon_plist_path() -> PathBuf {
    PathBuf::from(DAEMON_PLIST_PATH)
}

pub fn ui_plist_path() -> PathBuf {
    PathBuf::from(UI_PLIST_PATH)
}

pub fn remote_shell_plist_path() -> PathBuf {
    PathBuf::from(REMOTE_SHELL_PLIST_PATH)
}

pub fn remote_shell_queue_dir() -> PathBuf {
    PathBuf::from(REMOTE_SHELL_QUEUE_DIR)
}

pub fn installed_binary_path() -> PathBuf {
    PathBuf::from(INSTALLED_BINARY_PATH)
}

pub fn mas_binary_path() -> PathBuf {
    PathBuf::from(MAS_BINARY_PATH)
}

/// One config file, or `None` when it is absent, unreadable to this process, or not valid TOML.
fn read_file_config(path: &Path) -> Option<FileConfig> {
    let contents = std::fs::read_to_string(path).ok()?;
    toml::from_str::<FileConfig>(&contents).ok()
}

/// What `server.toml` holds: `api_base_url` and nothing else. Never the enrollment token — the file
/// is world-readable, and keeping the token out of reach of non-administrators is why it exists.
fn server_file_contents(api_base_url: &str) -> String {
    // `toml::Value`'s Display writes a quoted, escaped TOML string, so an address with a quote or
    // a backslash in it still parses back as itself.
    format!(
        "# Written by kintsugi-agent from config.toml on every check-in; edit config.toml instead.\napi_base_url = {}\n",
        toml::Value::String(api_base_url.to_string())
    )
}

/// Writes `server.toml` from the daemon's own resolved address, on every daemon run. Root only:
/// the directory it goes in is root's. Written to a temporary name and renamed, so the per-user
/// process can never read half of it, and only when it differs, so a check-in that changes nothing
/// writes nothing. Best-effort: a daemon that could not write it should still check in.
pub fn write_server_file(api_base_url: &str) {
    use std::os::unix::fs::PermissionsExt;

    let path = PathBuf::from(SERVER_FILE_PATH);
    let contents = server_file_contents(api_base_url);
    if std::fs::read_to_string(&path).is_ok_and(|existing| existing == contents) {
        return;
    }

    let staging = path.with_extension("toml.partial");
    let written = std::fs::write(&staging, &contents)
        .and_then(|()| std::fs::set_permissions(&staging, std::fs::Permissions::from_mode(0o644)))
        .and_then(|()| std::fs::rename(&staging, &path));
    match written {
        Ok(()) => crate::logging::info(&format!("wrote the server address for the per-user process to {}", path.display())),
        Err(err) => {
            let _ = std::fs::remove_file(&staging);
            crate::logging::warn(&format!("could not write {}: {err}", path.display()));
        }
    }
}

/// The parent of every path under `/Library/Application Support/kintsugi-agent` (config,
/// identity, queue, daemon log, check-in schedule) — a full removal (`self_removal`) deletes this
/// one directory rather than each file individually.
pub fn config_dir() -> PathBuf {
    PathBuf::from("/Library/Application Support/kintsugi-agent")
}

/// The mode `packaging/install.sh` gives `config.toml`: `root:admin 0640`. The file carries the
/// enrollment token until this host enrolls, so it stays administrator-only even though the
/// identity directory beside it is now readable by `staff`: the per-user process reads it when the
/// logged-in user is an administrator, and reads `server.toml` (the address alone, see
/// `SERVER_FILE_PATH`) when they are not. Not world-readable, because any other local account could
/// otherwise read a credential that enrolls a host into the fleet; it shipped `0644`. The Linux
/// agent's is `0600`, because there the per-user process never reads it.
pub const CONFIG_FILE_MODE: u32 = 0o640;

/// Re-asserts `CONFIG_FILE_MODE` and the `admin` group on `config.toml`, on every daemon run.
///
/// Here as well as in the installer because `self_update` replaces the binary and never re-runs the
/// installer, so a host already in the field has no other path from the `0644` it was installed
/// with. Best-effort, like `identity::repair_access`: a daemon that could not tighten a file mode
/// should still check in and say so, not stop.
pub fn repair_config_file_mode() {
    use std::os::unix::fs::PermissionsExt;

    let path = default_config_path();
    let Ok(metadata) = std::fs::metadata(&path) else {
        return;
    };

    if let Some(admin_gid) = crate::identity::admin_group_id() {
        // uid unchanged (None is "leave it alone"); the owner is already root.
        let _ = std::os::unix::fs::chown(&path, None, Some(admin_gid));
    }

    let current = metadata.permissions().mode() & 0o7777;
    if current == CONFIG_FILE_MODE {
        return;
    }

    match std::fs::set_permissions(&path, std::fs::Permissions::from_mode(CONFIG_FILE_MODE)) {
        Ok(()) => crate::logging::info(&format!("corrected the mode on {} from {current:04o} to {CONFIG_FILE_MODE:04o}", path.display())),
        Err(err) => crate::logging::warn(&format!("could not correct the mode on {} (currently {current:04o}): {err}", path.display())),
    }
}

/// Where the `--agent` (per-user) process keeps its own state: the cached patching policy and
/// scheduling state (next due time, delays used). Lives under the invoking user's home directory
/// since, unlike the root daemon's config, this process never runs as root.
///
/// Doesn't rely on `$HOME` alone: the installer loads this process via `launchctl bootstrap
/// gui/<uid>` from a root context (to pick it up immediately on a reinstall, without requiring a
/// log out/in), and a job started that way isn't guaranteed the same environment a normal login
/// session gets — `$HOME` in particular has been observed missing entirely in that case, which
/// previously made this fail silently before the agent's own log file even existed to explain
/// why. Falling back to the current effective user's actual passwd entry sidesteps environment
/// variables altogether.
pub fn user_state_dir() -> Result<PathBuf> {
    let home = std::env::var("HOME")
        .ok()
        .filter(|v| !v.trim().is_empty())
        .map(PathBuf::from)
        .or_else(home_dir_from_passwd)
        .context("could not determine the current user's home directory (checked $HOME and the passwd database)")?;

    Ok(home.join("Library/Application Support/kintsugi-agent"))
}

/// The short name of the account this process is running as.
///
/// Only meaningful in the per-user `--agent` process, which launchd starts in the logged-in user's
/// session — in the root daemon this is `root`, which is exactly the account that cannot authorize
/// a macOS install (see `os_update::install`). Read from the passwd database rather than `$USER`
/// for the reason `user_state_dir` prefers it: an environment variable is whatever the launching
/// context happened to set.
pub fn console_username() -> Option<String> {
    // SAFETY: getuid() cannot fail; getpwuid() returns either a valid pointer into a thread-local
    // static buffer (only read from here, immediately, before any other libc call in this thread
    // could invalidate it) or null. Same shape as `home_dir_from_passwd` below.
    unsafe {
        let passwd = libc::getpwuid(libc::getuid());
        if passwd.is_null() {
            return None;
        }
        let name = CStr::from_ptr((*passwd).pw_name).to_str().ok()?;
        (!name.is_empty()).then(|| name.to_string())
    }
}

fn home_dir_from_passwd() -> Option<PathBuf> {
    // SAFETY: getuid() cannot fail; getpwuid() returns either a valid pointer into a
    // thread-local static buffer (which we only read from, immediately, before any other libc
    // call in this thread could invalidate it) or null.
    unsafe {
        let passwd = libc::getpwuid(libc::getuid());
        if passwd.is_null() {
            return None;
        }
        let home_dir = CStr::from_ptr((*passwd).pw_dir).to_str().ok()?;
        if home_dir.is_empty() {
            return None;
        }
        Some(PathBuf::from(home_dir))
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn scratch(name: &str) -> PathBuf {
        let dir = std::env::temp_dir().join(format!("kintsugi-config-test-{name}-{}", std::process::id()));
        let _ = std::fs::remove_dir_all(&dir);
        std::fs::create_dir_all(&dir).unwrap();
        dir
    }

    #[test]
    fn the_server_file_carries_the_address_and_never_the_token() {
        let address = r#"https://kintsugi.example.com:8443/odd"path\"#;
        let parsed: FileConfig = toml::from_str(&server_file_contents(address)).expect("server.toml must parse");

        assert_eq!(parsed.api_base_url.as_deref(), Some(address));
        assert!(parsed.enrollment_token.is_none(), "server.toml is world-readable and must not carry the token");
    }

    #[test]
    fn an_unreadable_config_falls_back_to_the_server_file() {
        // What a standard user's per-user process sees: config.toml exists but is not theirs to
        // read (here: absent, which `read_file_config` treats the same way), server.toml is.
        let dir = scratch("fallback");
        let server = dir.join("server.toml");
        std::fs::write(&server, server_file_contents("https://kintsugi.example.com:9443")).unwrap();

        let config = Config::load_from(&[&dir.join("config.toml"), &server]);

        if std::env::var(ENV_OVERRIDE).is_err() {
            assert_eq!(config.api_base_url, "https://kintsugi.example.com:9443");
        }
        let _ = std::fs::remove_dir_all(&dir);
    }

    #[test]
    fn a_readable_config_wins_over_the_server_file() {
        let dir = scratch("precedence");
        let config_file = dir.join("config.toml");
        let server = dir.join("server.toml");
        std::fs::write(&config_file, "api_base_url = \"https://from-config.example.com\"\nenrollment_token = \"t\"\n").unwrap();
        std::fs::write(&server, server_file_contents("https://from-server-file.example.com")).unwrap();

        let config = Config::load_from(&[&config_file, &server]);

        if std::env::var(ENV_OVERRIDE).is_err() {
            assert_eq!(config.api_base_url, "https://from-config.example.com");
        }
        let _ = std::fs::remove_dir_all(&dir);
    }
}
