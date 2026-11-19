use std::fs;
use std::io::{Read, Write};
use std::os::unix::fs::{OpenOptionsExt, PermissionsExt};
use std::path::{Path, PathBuf};
use std::process::{Command, ExitStatus, Stdio};
use std::sync::{mpsc, Arc, Mutex};
use std::time::{Duration, Instant, SystemTime, UNIX_EPOCH};

use anyhow::{Context, Result};
use serde::{Deserialize, Serialize};
use time::format_description::well_known::Rfc3339;
use time::OffsetDateTime;

use crate::config::{self, Config};
use crate::identity::{self, AgentIdentity};
use crate::logging;
use crate::self_service;
use crate::system_info;

/// Mirrors the backend's `UpgradeStatusDto` — see
/// Kintsugi.Application/UpgradePaths/UpgradeStatusDto.cs. `latestVersion`/`updateAvailable`
/// are trusted straight from the backend now: the server runs each script's own
/// `--update-version` mode itself (no AI call, and no need for this agent to duplicate that
/// check), so its answer is the authoritative one, not just a starting point this agent used to
/// re-verify. Fields this agent still has no use for (hostname, serialNumber, sourceUrl,
/// checkedUtc) stay omitted, since serde only requires the fields actually named here to be
/// present.
#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct UpgradeStatus {
    pub application_name: String,
    /// Reported back alongside a failure, so the Failed Updates screen can say what was installed
    /// when the upgrade was attempted — see `report_patch_failure`.
    pub installed_version: String,
    pub latest_version: Option<String>,
    pub update_available: bool,
    pub method: UpgradeMethod,
    /// CFBundleIdentifier, when known — required to invoke `script` at all, since it needs
    /// `--appId` for both its own safety check and its CLI contract.
    pub application_identifier: Option<String>,
    /// The package-manager command for a `Method::PackageManagerCommand` row (e.g. `brew upgrade
    /// firefox`) — run as-is via a shell, as the logged-in (admin) user, never as root: Homebrew
    /// itself refuses to run as root. Only ever populated for an unrecognized package manager's
    /// legacy row now — a recognized one (Homebrew) gets a `Method::Script` row instead, see below.
    pub command: Option<String>,
    #[allow(dead_code)]
    pub notes: Option<String>,
    /// A bash script implementing a durable `--update-version` / `--update` CLI for a
    /// `Method::Script` row — see `run_script` — absent for every other method. Either AI-authored,
    /// or a fixed one the server writes itself for a recognized package manager (e.g. Homebrew).
    pub script: Option<String>,
    /// Base64 DER ECDSA-SHA256 signature over `script`'s bytes, from the server's own
    /// artifact-signing key — see `identity::verify_artifact_signature`. `is_patchable` refuses to
    /// treat a `Script` row as runnable at all unless this checks out.
    pub script_signature: Option<String>,
    /// Same as `script_signature`, but over `command`.
    pub command_signature: Option<String>,
    /// The package manager that owns this installation ("Homebrew"), or `None` for a standalone
    /// application whose script was AI-researched. Decides *which process* runs a `Script` row —
    /// see `runs_as_root`.
    pub package_manager: Option<String>,
    /// `"Install"` or `"Uninstall"` on a software title's **action row**, `None` on an ordinary
    /// upgrade row — see `Action` and `act_on_assigned`. A string rather than an enum, because an
    /// enum would fail the *whole* work list on a value a newer server adds, and this agent must
    /// ignore a row it does not understand rather than stop patching. Every new field below is
    /// `#[serde(default)]` so a server from before software titles parses exactly as it always did.
    /// Kept identical in the other two agents.
    #[serde(default)]
    pub action: Option<String>,
    /// Offered to the logged-in user in the menu bar's "Install Software" submenu, and never acted
    /// on at check-in — see `act_on_assigned` and `self_service`.
    #[serde(default)]
    pub self_service: bool,
    /// Base64 DER ECDSA-SHA256 over `canonical_action_text`, made by the server when an
    /// administrator saved the title. It is what stops the one signed winget or Homebrew script
    /// becoming "install whatever id the database names" — the script signature covers the script
    /// text, and this covers what it is being pointed at.
    #[serde(default)]
    pub action_signature: Option<String>,
    /// The secrets this row's script needs, each delivered as `KINTSUGI_SECRET_<NAME>` and each
    /// signed on its own — see `ScriptSecret`. Only ever present on an action row. `null` and
    /// absent both read as empty, since the server sends `null` on every ordinary row.
    #[serde(default, deserialize_with = "null_as_empty")]
    pub secrets: Vec<ScriptSecret>,
}

/// Whether this row's upgrade has to be run by the root daemon (through `queue`) rather than by the
/// per-user process asking. The dividing line is who owns the installation:
///
/// - A **Homebrew** row stays with the logged-in user: Homebrew refuses to run as root outright
///   ("Running Homebrew as root is extremely dangerous and no longer supported"), and its installs
///   are user-owned, so the logged-in user is the right — and only — process for it. A legacy
///   `PackageManagerCommand` row is a bare `brew upgrade ...` for the same reason. That covers
///   Homebrew's *own* row too, which is recognized by name (`system_info::HOMEBREW_NAME`) rather
///   than by owner: the inventory reports the manager with no `packageManager` of its own, so on
///   that field alone it reads as an AI-researched row and was queued to the daemon — which
///   rightly refused it, and every patch cycle then logged Homebrew as failed. Its upgrade is the
///   same shared script every formula runs (`brew update` is Homebrew upgrading itself — see
///   `HomebrewUpgradeScript` on the server), as the same user.
/// - An **AI-researched** row (no package manager) installs into `/Applications` the way the
///   server's prompt tells it to — replace the bundle in place, or `installer -pkg ... -target /` —
///   and a bundle that arrived by MDM, `.pkg` or any installer that asked for a password is owned
///   by `root:wheel`. Run as the user, the script's `rm` prints `Permission denied` for every file
///   in the bundle and the old version stays; that is what took Ollama down on the fleet's own
///   Macs. The prompt now writes these scripts *for* root (see
///   `AiUpgradePathResearchClient.BuildScriptGenerationPrompt`), so this is also the only context
///   they are tested in.
/// - An **App Store** row goes to root as well, for a reason that is the mirror image of Homebrew's.
///   Since Apple's fix for CVE-2025-43411 the install half of a store update needs root, while the
///   download half needs the logged-in user's store session; the per-user process is one of those
///   and has no way to become the other (its `sudo` has no TTY to answer), whereas root can be both
///   — `launchctl asuser` puts it inside the user's session and `mas` drops to that user for the
///   store half. The server's `AppStoreUpgradeScript` does exactly that dance, and it is written for
///   root: run as the user it refuses on its first line. Recognized by the same name the inventory
///   reports the manager under (`system_info::APP_STORE_NAME`), which is also the string the
///   server's `PackageManagerCatalog.AppStore` keys the row's bucket by.
///
/// - Every **action row** — a software title's `--install` or `--uninstall` — goes to root,
///   Homebrew's included. Those scripts are written for root by contract (they run unattended from
///   the daemon's own timer check-in, where there is no user to run them as), and the server's
///   Homebrew builder finds the prefix owner and drops to them itself for exactly that reason. One
///   rule for every action row also means "Run now" and a click in the "Install Software" submenu
///   reach the daemon by the same `AppPatch` request, with no new request kind.
///
/// The daemon asks this same question before running a request (see `main::DaemonRequestHandler`)
/// and refuses a Homebrew row, so a forged request cannot get `brew` run as root either.
pub fn runs_as_root(status: &UpgradeStatus) -> bool {
    status.is_action()
        || status.method == UpgradeMethod::Script
        && !status.application_name.eq_ignore_ascii_case(system_info::HOMEBREW_NAME)
        && status.package_manager.as_deref().is_none_or(|manager| manager.eq_ignore_ascii_case(system_info::APP_STORE_NAME))
}

/// Mirrors Kintsugi.Domain.Enums.UpgradeMethod. Deserializes from the backend's plain enum
/// member names (`"Script"`, `"PackageManagerCommand"`, ...), which is `serde`'s default for a
/// unit-only enum — unlike `policy::TimeUnit`, no manual ordinal mapping is needed here.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Deserialize)]
pub enum UpgradeMethod {
    Unknown,
    DirectDownload,
    PackageManagerCommand,
    ManualSteps,
    Script,
}

/// Fetches this host's known upgrade paths from the backend. Failures here are the caller's to
/// handle as best-effort: a bad or unreachable response shouldn't stop the agent from having
/// already registered the host and its installed applications, which every other consumer (the
/// Applications page) depends on regardless of whether upgrade research has run yet.
pub fn fetch_upgrade_statuses(
    client: &reqwest::blocking::Client,
    config: &Config,
    serial_number: &str,
) -> Result<Vec<UpgradeStatus>> {
    let response = crate::get_with_retry("this host's upgrade paths", || {
        client
            .get(config.upgrade_paths_url())
            .query(&[("serialNumber", serial_number)])
    })?;

    if !response.status().is_success() {
        anyhow::bail!("request rejected (HTTP {})", response.status());
    }

    response
        .json::<Vec<UpgradeStatus>>()
        .context("could not parse response")
}

#[derive(Debug, Serialize)]
#[serde(rename_all = "camelCase")]
struct ReportPatchResultRequest<'a> {
    serial_number: &'a str,
    application_name: &'a str,
    new_version: &'a str,
}

/// Tells the server this application was just successfully patched to `new_version`, so its
/// record of what's installed reflects that immediately rather than waiting on this host's next
/// full inventory report (see `main::collect_installed_applications`). Best-effort: the patch
/// itself already succeeded locally by the time this is called, so a failure here is only logged
/// — there is nothing to roll back, and the next full inventory report reconciles the server's
/// record regardless.
pub fn report_patch_result(client: &reqwest::blocking::Client, config: &Config, serial_number: &str, application_name: &str, new_version: &str) {
    let request = ReportPatchResultRequest { serial_number, application_name, new_version };

    match client.post(config.patch_result_url()).json(&request).send() {
        Ok(response) if response.status().is_success() => {
            logging::info(&format!("reported successful patch of {application_name} to {new_version}"));
        }
        Ok(response) => {
            logging::warn(&format!(
                "server rejected patch-result report for {application_name} (HTTP {})",
                response.status()
            ));
        }
        Err(err) => {
            logging::warn(&format!("could not report successful patch of {application_name} to the server: {err:#}"));
        }
    }
}

#[derive(Debug, Serialize)]
#[serde(rename_all = "camelCase")]
struct ReportPatchFailureRequest<'a> {
    serial_number: &'a str,
    application_name: &'a str,
    installed_version: &'a str,
    attempted_version: Option<&'a str>,
    failed_utc: String,
    details: String,
}

/// The most of a failing script's output this agent will send to the server.
///
/// A failing script's stderr is unbounded — a loop printing a permission error per file in a
/// bundle runs to megabytes — and it arrives here untruncated, since `run_script` bails with the
/// whole of it. This has to stay comfortably *below* the server's own ceiling
/// (`ReportPatchFailureCommandValidator.MaxDetailsLength`, currently 16000): a report longer than
/// the validator accepts comes back as a 400 and the failure is lost, silently, for exactly the
/// noisiest failures — which are the ones most worth reading. Raise the server's figure before
/// raising this one, never after. Kept identical in the other two agents.
const MAX_REPORTED_FAILURE_BYTES: usize = 4000;

/// Tells the server this application's upgrade ran and failed, so it turns up on the admin UI's
/// Failed Updates screen with the date and the output — where the script can be repaired by the AI
/// or by hand and re-signed.
///
/// Called by whichever process actually ran the thing that failed, exactly like
/// `report_patch_result`: the root daemon for an AI-researched script (see
/// `main::DaemonRequestHandler::patch_application`), this process for a Homebrew one. The per-user
/// side must **not** also report a failure that came back from the daemon through `queue`, or every
/// root-run failure is recorded twice.
///
/// Deliberately not called for a patch that never ran — an application with no signed, patchable
/// path, or a row the daemon refuses on `runs_as_root` grounds. Those are configuration problems
/// rather than bugs in a script, and a screen full of them hides the ones the AI can actually fix.
///
/// Best-effort, like every other report: the failure has already happened and been logged locally,
/// and the next patch cycle reports it again if the server was unreachable this time.
pub fn report_patch_failure(
    client: &reqwest::blocking::Client,
    config: &Config,
    serial_number: &str,
    status: &UpgradeStatus,
    error: &anyhow::Error,
) {
    report_failure(
        client,
        config,
        serial_number,
        &status.application_name,
        &status.installed_version,
        status.latest_version.as_deref(),
        error,
    );
}

/// The POST itself, with the application taken as three plain fields rather than an
/// [`UpgradeStatus`].
///
/// Split out of `report_patch_failure` so `os_update::report_failed` can file a failed macOS
/// install on the same screen, through the same route, with the same truncation rule — an OS update
/// has no upgrade-path row and so no `UpgradeStatus` to describe it. `.claude/rules/hand-mirrored-dtos.md`
/// is the reason this is a shared function rather than a second request struct alongside
/// `ReportPatchFailureRequest`: one wire shape, hand-mirrored against `ReportPatchFailureCommand`
/// in exactly one place, is the whole point of that rule.
pub fn report_failure(
    client: &reqwest::blocking::Client,
    config: &Config,
    serial_number: &str,
    application_name: &str,
    installed_version: &str,
    attempted_version: Option<&str>,
    error: &anyhow::Error,
) {
    // The host's own clock, not the server's arrival time: a Mac that patched overnight and could
    // not reach the server until morning would otherwise report the failure as having happened
    // when the network came back.
    let failed_utc = match OffsetDateTime::now_utc().format(&Rfc3339) {
        Ok(formatted) => formatted,
        Err(err) => {
            logging::warn(&format!("could not format the failure timestamp, not reporting: {err:#}"));
            return;
        }
    };

    let request = ReportPatchFailureRequest {
        serial_number,
        application_name,
        installed_version,
        attempted_version,
        failed_utc,
        details: truncate_for_report(&format!("{error:#}")),
    };

    match client.post(config.patch_failure_url()).json(&request).send() {
        Ok(response) if response.status().is_success() => {
            logging::info(&format!("reported the failed patch of {application_name} to the server"));
        }
        Ok(response) => {
            logging::warn(&format!(
                "server rejected the patch-failure report for {application_name} (HTTP {})",
                response.status()
            ));
        }
        Err(err) => {
            logging::warn(&format!("could not report the failed patch of {application_name} to the server: {err:#}"));
        }
    }
}

/// `truncate_for_log`'s rule, at the reporting limit rather than the logging one, and keeping the
/// *tail* rather than the head: a script's last words are where its failure is, while its first
/// are the same preamble every run prints.
fn truncate_for_report(text: &str) -> String {
    let trimmed = text.trim();
    if trimmed.len() <= MAX_REPORTED_FAILURE_BYTES {
        return trimmed.to_string();
    }

    // Back up to the nearest char boundary — a plain byte-index slice could otherwise land inside
    // a multi-byte UTF-8 character and panic.
    let mut cut = trimmed.len() - MAX_REPORTED_FAILURE_BYTES;
    while !trimmed.is_char_boundary(cut) {
        cut += 1;
    }

    format!("... [truncated, {cut} earlier byte(s) omitted]\n{}", &trimmed[cut..])
}

/// Whether `patch_one` has anything to actually do for this row — used to build the list a patch
/// cycle will actually work through (and size its progress bar against), rather than counting
/// e.g. an unresolved or manual-steps-only path that can never advance it. A `Script` or
/// `PackageManagerCommand` row whose content doesn't carry a signature that verifies against
/// `identity`'s pinned artifact-signing key is treated as not patchable at all, the same as if it
/// had no command/script present — content that never went through the server's real signing step
/// (e.g. a row written straight to the database) is never eligible to run.
pub fn is_patchable(status: &UpgradeStatus, identity: &AgentIdentity) -> bool {
    status.update_available
        && match status.method {
            UpgradeMethod::PackageManagerCommand => status.command.is_some() && verify_signed(identity, &status.command, &status.command_signature),
            UpgradeMethod::Script => {
                status.script.is_some()
                    && status.application_identifier.is_some()
                    && verify_signed(identity, &status.script, &status.script_signature)
            }
            _ => false,
        }
}

fn verify_signed(identity: &AgentIdentity, content: &Option<String>, signature: &Option<String>) -> bool {
    match (content, signature) {
        (Some(content), Some(signature)) => match identity::verify_artifact_signature(identity, content, signature) {
            Ok(()) => true,
            Err(err) => {
                logging::error(&format!("refusing to trust unsigned/tampered content: {err:#}"));
                false
            }
        },
        _ => false,
    }
}

// ---------------------------------------------------------------------------------------------
// Software titles: install and uninstall rows on the same work list as upgrades.
//
// An administrator can make a title Required (install it where it is missing), Prohibited (remove
// it where it is present) or Self-service (offer it in the menu). The server appends an **action
// row** for each to this host's ordinary work list, with `updateAvailable: false` so an agent from
// before this ignores it — `is_patchable` requires `update_available`. Everything here is kept
// identical in all three agents, comments included, apart from the lines that name a platform.
// ---------------------------------------------------------------------------------------------

/// How long an action row's script may run before it is killed — the contract's `ACTION_TIMEOUT`,
/// shared with the server's prompt and the other two agents.
///
/// Without a bound, one installer that hangs on a prompt nobody can see stops this host checking in
/// for good: the check-in that started it never finishes, and with it `self_update`, which is the
/// only way a fix could ever reach the host. Ninety minutes is long enough for Office's whole
/// download over a slow link. Upgrade rows keep no timeout, exactly as before.
pub const ACTION_TIMEOUT: Duration = Duration::from_secs(ACTION_TIMEOUT_SECS);
const ACTION_TIMEOUT_SECS: u64 = 90 * 60;

/// How long the per-user half waits for the privileged half to answer an action request: the
/// script's own bound plus a margin for the fetch, the verification and the inventory upload that
/// follow it. Longer than the wait for an upgrade on purpose — waiting less than the script may
/// run means giving up on an answer that is still coming.
pub const ACTION_WAIT: Duration = Duration::from_secs(ACTION_TIMEOUT_SECS + 10 * 60);

/// What an action row asks for. Parsed from `UpgradeStatus::action` rather than deserialized into,
/// for the reason given on that field.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Action {
    Install,
    Uninstall,
}

impl Action {
    /// Exactly the two strings the server sends. Case-sensitive, because the same string is part
    /// of the signed `canonical_action_text`, and a value that differs in case would verify against
    /// nothing anyway.
    pub fn parse(value: &str) -> Option<Self> {
        match value {
            "Install" => Some(Action::Install),
            "Uninstall" => Some(Action::Uninstall),
            _ => None,
        }
    }

    /// The script mode this action runs, in place of `--update`. Both modes are idempotent by
    /// contract: a host already in the wanted state exits 0 having changed nothing.
    fn mode_flag(self) -> &'static str {
        match self {
            Action::Install => "--install",
            Action::Uninstall => "--uninstall",
        }
    }

    /// What `report_outcome` sends as the new version when the server gave no `latestVersion`, so a
    /// Failed Updates row for this title still closes on success.
    fn reported_version(self) -> &'static str {
        match self {
            Action::Install => "installed",
            Action::Uninstall => "removed",
        }
    }

    /// The verb a log line or a notification uses.
    pub fn verb(self) -> &'static str {
        match self {
            Action::Install => "install",
            Action::Uninstall => "uninstall",
        }
    }
}

/// One secret an action row's script needs — mirrors the server's `ScriptSecretDto`.
///
/// The value is plaintext on the wire and in the server's database, which is the house rule (the
/// database is not a secret store); `signature` is what makes it trustworthy here. It covers
/// `canonical_secret_text`, made when an administrator set the value, so a value edited straight
/// into the database — a CrowdStrike CID swapped for somebody else's — verifies against nothing and
/// the row is refused.
///
/// **The value must never be logged, printed, queued or cached.** `Debug` is written by hand for
/// exactly that reason: `UpgradeStatus` derives `Debug`, and a derived one here would put every
/// secret into the first `{status:?}` anybody writes.
#[derive(Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ScriptSecret {
    pub name: String,
    pub value: String,
    pub signature: String,
}

impl std::fmt::Debug for ScriptSecret {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("ScriptSecret").field("name", &self.name).field("value", &"[redacted]").finish()
    }
}

/// `secrets` arrives as `null` on every ordinary row, which `#[serde(default)]` alone does not
/// accept — default covers an *absent* field, not a present `null`.
fn null_as_empty<'de, D, T>(deserializer: D) -> std::result::Result<Vec<T>, D::Error>
where
    D: serde::Deserializer<'de>,
    T: Deserialize<'de>,
{
    Ok(Option::<Vec<T>>::deserialize(deserializer)?.unwrap_or_default())
}

/// The prefix every secret's environment variable carries — `KINTSUGI_SECRET_CROWDSTRIKE_CID`.
/// Part of the script contract the server's prompt describes, so a script can rely on it.
const SECRET_ENV_PREFIX: &str = "KINTSUGI_SECRET_";

/// The server's own rule for a secret name, `^[A-Z][A-Z0-9_]{0,63}$`, checked again here because
/// the name becomes an environment variable's name: one carrying `=` or a NUL would be a different
/// variable, or no variable, by the time the script saw it.
fn is_valid_secret_name(name: &str) -> bool {
    let mut chars = name.chars();
    name.len() <= 64
        && chars.next().is_some_and(|first| first.is_ascii_uppercase())
        && chars.all(|c| c.is_ascii_uppercase() || c.is_ascii_digit() || c == '_')
}

/// What the server signs for an action row, byte for byte — the contract's canonical text, which
/// the server's `SoftwareTitle.CanonicalActionText()` builds from the same parts:
///
/// ```text
/// kintsugi-action-v1\n<action>\n<packageManager or empty>\n<applicationName>\n<applicationIdentifier>\n<secret names, sorted, comma-joined>
/// ```
///
/// The names are sorted here rather than trusted in the order they arrive, so a server that lists
/// them differently from how it signed them still verifies. Sorted by byte (`str`'s `Ord`), which
/// is what the server's ordinal sort produces for names limited to `[A-Z0-9_]`.
pub fn canonical_action_text(
    action: &str,
    package_manager: Option<&str>,
    application_name: &str,
    application_identifier: &str,
    secret_names: &[&str],
) -> String {
    let mut names = secret_names.to_vec();
    names.sort_unstable();
    format!(
        "kintsugi-action-v1\n{action}\n{}\n{application_name}\n{application_identifier}\n{}",
        package_manager.unwrap_or(""),
        names.join(",")
    )
}

/// What the server signs for a secret's value — `"kintsugi-secret-v1\n" + name + "\n" + value`.
pub fn canonical_secret_text(name: &str, value: &str) -> String {
    format!("kintsugi-secret-v1\n{name}\n{value}")
}

impl UpgradeStatus {
    /// Whether this is a software title's action row rather than an upgrade — any value at all,
    /// known or not, since an unknown one must not be mistaken for an upgrade either.
    pub fn is_action(&self) -> bool {
        self.action.is_some()
    }
}

/// Everything that has to hold before an action row may run, as one checked answer: the action is
/// one this agent knows, it is a signed `Script` row with an identifier, and the script's
/// signature, the action's signature and every secret's signature all verify against the pinned
/// key. `is_actionable` asks it to build lists; `patch_one` asks it again immediately before
/// running, for the reason `patch_one`'s own comment gives.
pub fn verify_action(status: &UpgradeStatus, identity: &AgentIdentity) -> Result<Action> {
    let action_name = status.action.as_deref().context("this is not an action row")?;
    let action = Action::parse(action_name).with_context(|| format!("unknown action {action_name:?}"))?;

    if status.method != UpgradeMethod::Script {
        anyhow::bail!("an action row must be a Script row, not {:?}", status.method);
    }
    let script = status.script.as_deref().context("no script was provided for this action")?;
    let script_signature = status
        .script_signature
        .as_deref()
        .context("no signature was provided for this script — refusing to run it")?;
    identity::verify_artifact_signature(identity, script, script_signature)
        .context("script signature verification failed — refusing to run it")?;

    let app_id = status
        .application_identifier
        .as_deref()
        .context("no application identifier known, but the script requires --appId")?;

    for secret in &status.secrets {
        if !is_valid_secret_name(&secret.name) {
            anyhow::bail!("secret name {:?} is not a valid name — refusing to run it", secret.name);
        }
        identity::verify_artifact_signature(identity, &canonical_secret_text(&secret.name, &secret.value), &secret.signature)
            .with_context(|| format!("secret {} failed signature verification — refusing to run it", secret.name))?;
    }

    let names: Vec<&str> = status.secrets.iter().map(|secret| secret.name.as_str()).collect();
    let action_signature = status
        .action_signature
        .as_deref()
        .context("no action signature was provided — refusing to run it")?;
    let canonical = canonical_action_text(action_name, status.package_manager.as_deref(), &status.application_name, app_id, &names);
    identity::verify_artifact_signature(identity, &canonical, action_signature)
        .context("action signature verification failed — refusing to run it")?;

    Ok(action)
}

/// `is_patchable`'s counterpart for action rows — whether `patch_one` may run this row's
/// `--install` or `--uninstall`. See `verify_action` for what has to hold.
///
/// An action this agent does not know is skipped without a word: a newer server may add one, and
/// an error line every hour for something this build cannot do says nothing anyone can act on. A
/// known action that fails verification *is* logged, because that is a tampered row or a title
/// whose secrets changed after it was signed, and somebody should look.
pub fn is_actionable(status: &UpgradeStatus, identity: &AgentIdentity) -> bool {
    if !status.action.as_deref().is_some_and(|action| Action::parse(action).is_some()) {
        return false;
    }
    match verify_action(status, identity) {
        Ok(_) => true,
        Err(err) => {
            logging::error(&format!("refusing to act on {}: {err:#}", status.application_name));
            false
        }
    }
}

/// Finds the one row the privileged half may run for `application_name` — an upgrade that is
/// patchable, or an action row that is actionable, self-service ones included.
///
/// The lookup exists because of `queue`: an `AppPatch` request carries only a name, never anything
/// executable, so this is where that name is turned back into something runnable — against a fresh
/// server response and fresh signature checks, never against anything the requester supplied. That
/// one property serves "Patch Now", a forced run and a click in the "Install Software" submenu
/// alike, which is why none of them needed a new request kind. Names are compared
/// case-insensitively for the same reason the server's own lookups are.
pub fn find_runnable<'a>(statuses: &'a [UpgradeStatus], application_name: &str, identity: &AgentIdentity) -> Option<&'a UpgradeStatus> {
    statuses.iter().find(|status| {
        status.application_name.eq_ignore_ascii_case(application_name)
            && if status.is_action() { is_actionable(status, identity) } else { is_patchable(status, identity) }
    })
}

/// The environment variables an action row's script receives — `KINTSUGI_SECRET_<NAME>` for each
/// declared secret. Empty for every other row.
fn secret_env(status: &UpgradeStatus) -> Vec<(String, String)> {
    status
        .secrets
        .iter()
        .map(|secret| (format!("{SECRET_ENV_PREFIX}{}", secret.name), secret.value.clone()))
        .collect()
}

/// Replaces every secret value in `text` with `[secret NAME]`.
///
/// Applied to a script's output before anything else sees it: the agent's log, the error text, and
/// the failure tail uploaded to the Failed Updates screen (and on Linux, journald, which a log
/// collection uploads). A script is told never to print a secret, but an installer that echoes its
/// command line will, and this is the one place every path out passes through.
///
/// Longest values first, so a secret that contains another is replaced whole rather than leaving
/// the rest of itself behind. An empty value is skipped — replacing the empty string would insert
/// the marker between every character.
fn redact(text: &str, env: &[(String, String)]) -> String {
    let mut secrets: Vec<&(String, String)> = env.iter().filter(|(_, value)| !value.is_empty()).collect();
    secrets.sort_by_key(|(_, value)| std::cmp::Reverse(value.len()));

    let mut redacted = text.to_string();
    for (key, value) in secrets {
        let name = key.strip_prefix(SECRET_ENV_PREFIX).unwrap_or(key);
        redacted = redacted.replace(value.as_str(), &format!("[secret {name}]"));
    }
    redacted
}

/// Reports one row's outcome to the server, whichever kind of row it was: a success through
/// `report_patch_result`, a failure through `report_patch_failure`.
///
/// One helper rather than the two calls repeated at each site, because an action row needs a new
/// version to send even when the server gave none: `latest_version` if it is known, otherwise
/// `"installed"` or `"removed"`. Without that, the success of an install whose version nobody knows
/// would never be reported, and the Failed Updates row an earlier attempt left would never close.
pub fn report_outcome(client: &reqwest::blocking::Client, config: &Config, serial_number: &str, status: &UpgradeStatus, outcome: &Result<()>) {
    match outcome {
        Ok(()) => match reported_version(status) {
            Some(new_version) => report_patch_result(client, config, serial_number, &status.application_name, &new_version),
            None => logging::warn(&format!(
                "patched {} successfully, but no latest_version was known to report to the server",
                status.application_name
            )),
        },
        Err(err) => report_patch_failure(client, config, serial_number, status, err),
    }
}

fn reported_version(status: &UpgradeStatus) -> Option<String> {
    let latest = status.latest_version.clone().filter(|version| !version.is_empty());
    match status.action.as_deref().and_then(Action::parse) {
        Some(action) => Some(latest.unwrap_or_else(|| action.reported_version().to_string())),
        None => latest,
    }
}

/// The timer-only action pass: runs this host's Required and Prohibited titles silently from the
/// privileged half, and refreshes the cache the menu's "Install Software" submenu is built from.
///
/// 1. Fetches the work list.
/// 2. Writes the self-service offers — names and versions, **never secrets** — to
///    `self_service_path`, so the per-user half can offer them without a network call of its own.
///    Written on every successful fetch, empty included, so a title withdrawn from self-service
///    leaves the menu at the next check-in.
/// 3. Runs every actionable row that is not self-service with `patch_one`, and reports each.
/// 4. If anything ran, re-uploads the inventory through `report_inventory`, so the application
///    appears (or disappears) on the server now, and from then on is patched like any other.
///
/// **Only ever from the hourly timer check-in**, never from "Check In Now": the person who clicked
/// that is waiting on an answer, and an install can take ninety minutes. Each agent's call site says
/// how it tells the two apart.
///
/// Never fails the check-in: an unreachable server leaves the cache as it was, and a failed action
/// is reported to Failed Updates and retried at the next hour.
pub fn act_on_assigned(
    client: &reqwest::blocking::Client,
    config: &Config,
    serial_number: &str,
    identity: &AgentIdentity,
    self_service_path: &Path,
    report_inventory: &mut dyn FnMut(),
) {
    let statuses = match fetch_upgrade_statuses(client, config, serial_number) {
        Ok(statuses) => statuses,
        Err(err) => {
            logging::warn(&format!("could not fetch this host's software titles; acting on none this check-in: {err:#}"));
            return;
        }
    };

    let (offers, assigned): (Vec<&UpgradeStatus>, Vec<&UpgradeStatus>) = statuses
        .iter()
        .filter(|status| status.is_action() && is_actionable(status, identity))
        .partition(|status| status.self_service);

    let offers: Vec<self_service::Offer> = offers.into_iter().map(self_service::Offer::from_status).collect();
    if let Err(err) = self_service::write(self_service_path, &offers) {
        logging::warn(&format!("could not write the self-service offers for the menu: {err:#}"));
    }

    let mut ran = false;
    for status in assigned {
        // Always there: `is_actionable` has just parsed it.
        let Some(action) = status.action.as_deref().and_then(Action::parse) else { continue };
        logging::info(&format!(
            "about to {} {}, as an administrator assigned, at this check-in",
            action.verb(),
            status.application_name
        ));

        let outcome = patch_one(status, identity);
        match &outcome {
            Ok(()) => logging::info(&format!("{} of {} succeeded", action.verb(), status.application_name)),
            Err(err) => logging::error(&format!("could not {} {}: {err:#}", action.verb(), status.application_name)),
        }
        report_outcome(client, config, serial_number, status, &outcome);
        ran = true;
    }

    if ran {
        report_inventory();
    }
}

/// How often a script with a timeout is checked for having finished.
const SCRIPT_POLL_INTERVAL: Duration = Duration::from_millis(500);

/// How long the output readers are given to finish once the script itself has exited or been
/// killed. A script that leaves something running in the background which still holds its output
/// pipe would otherwise keep them reading for ever; what had arrived by then is kept.
const OUTPUT_GRACE: Duration = Duration::from_secs(5);

/// What running a script came to: its exit status, or `None` when it was killed at the timeout.
struct Finished {
    status: Option<ExitStatus>,
    stdout: Vec<u8>,
    stderr: Vec<u8>,
}

/// Reads one of the child's output streams to its end on a thread of its own, into a buffer the
/// caller can take whatever is in once `OUTPUT_GRACE` runs out — both streams have to be drained
/// while the script runs or a chatty one fills its pipe and blocks for ever.
fn drain(stream: Option<impl Read + Send + 'static>) -> (Arc<Mutex<Vec<u8>>>, mpsc::Receiver<()>) {
    let buffer = Arc::new(Mutex::new(Vec::new()));
    let (done_tx, done_rx) = mpsc::channel();
    if let Some(mut stream) = stream {
        let buffer = buffer.clone();
        std::thread::spawn(move || {
            let mut chunk = [0u8; 8192];
            while let Ok(read) = stream.read(&mut chunk) {
                if read == 0 {
                    break;
                }
                buffer.lock().unwrap_or_else(|poisoned| poisoned.into_inner()).extend_from_slice(&chunk[..read]);
            }
            let _ = done_tx.send(());
        });
    }
    (buffer, done_rx)
}

fn take_drained((buffer, done): (Arc<Mutex<Vec<u8>>>, mpsc::Receiver<()>), deadline: Instant) -> Vec<u8> {
    let _ = done.recv_timeout(deadline.saturating_duration_since(Instant::now()));
    std::mem::take(&mut *buffer.lock().unwrap_or_else(|poisoned| poisoned.into_inner()))
}

/// Runs `command` to completion, or for at most `timeout`.
///
/// With no timeout this is `Command::output`, exactly as every upgrade has always run. With one,
/// the child is spawned and polled with `try_wait` — no new crate — and killed once the bound is
/// reached: its whole process tree, since what hangs is usually the installer the script started
/// rather than the script itself (see `kill_tree`).
fn run_bounded(mut command: Command, timeout: Option<Duration>) -> Result<Finished> {
    let Some(timeout) = timeout else {
        let output = command.output().context("failed to execute script")?;
        return Ok(Finished { status: Some(output.status), stdout: output.stdout, stderr: output.stderr });
    };

    isolate_process_tree(&mut command);
    let mut child = command
        .stdin(Stdio::null())
        .stdout(Stdio::piped())
        .stderr(Stdio::piped())
        .spawn()
        .context("failed to execute script")?;
    let stdout = drain(child.stdout.take());
    let stderr = drain(child.stderr.take());

    let started = Instant::now();
    let status = loop {
        if let Some(status) = child.try_wait().context("failed to wait for the script")? {
            break Some(status);
        }
        if started.elapsed() >= timeout {
            kill_tree(&mut child);
            let _ = child.wait();
            break None;
        }
        std::thread::sleep(SCRIPT_POLL_INTERVAL);
    };

    let deadline = Instant::now() + OUTPUT_GRACE;
    Ok(Finished { status, stdout: take_drained(stdout, deadline), stderr: take_drained(stderr, deadline) })
}

/// Puts the script in a process group of its own, so `kill_tree` reaches everything it started.
/// The Linux agent does the same; the Windows agent has no process groups to speak of and kills
/// the tree with `taskkill /T` instead.
fn isolate_process_tree(command: &mut Command) {
    use std::os::unix::process::CommandExt;
    command.process_group(0);
}

/// Kills a timed-out script and everything it started. Killing only the shell would leave the
/// hung installer running with the output pipe still open, which is the very thing being bounded.
fn kill_tree(child: &mut std::process::Child) {
    if let Ok(pid) = i32::try_from(child.id()) {
        // SAFETY: `kill` touches no memory. A negative pid signals the whole group, which
        // `isolate_process_tree` made this child the leader of, so it cannot reach anything else.
        unsafe {
            libc::kill(-pid, libc::SIGKILL);
        }
    }
    let _ = child.kill();
}

/// Runs the actual update for one already-selected (`is_patchable`) application, as whichever user
/// this process itself runs as: a package-manager command (e.g. `brew upgrade firefox`) for
/// `Method::PackageManagerCommand`, or the script's `--update` mode for `Method::Script`. Which
/// process that is — the logged-in user for a Homebrew row, the root daemon for an AI-researched
/// one — is `runs_as_root`'s decision, made by both callers (`patch_cycle::run_patches` and
/// `main::DaemonRequestHandler`); this function does not check it, because it cannot tell which
/// process it is in and the daemon's refusal has to happen before a request is trusted at all.
/// Re-verifies the signature right before running it, rather than trusting that the caller already
/// checked via `is_patchable` — the one function that actually executes something is the one that
/// shouldn't ever skip that check, even if every current caller happens to call it correctly.
///
/// A software title's action row comes through here too, and runs `--install` or `--uninstall`
/// instead — see `prepare_script_run`, which re-verifies the action and every secret as well.
pub fn patch_one(status: &UpgradeStatus, identity: &AgentIdentity) -> Result<()> {
    match status.method {
        UpgradeMethod::PackageManagerCommand => {
            let command = status
                .command
                .as_deref()
                .context("no command was provided for this package-manager path")?;
            let signature = status
                .command_signature
                .as_deref()
                .context("no signature was provided for this command — refusing to run it")?;
            identity::verify_artifact_signature(identity, command, signature)
                .context("command signature verification failed — refusing to run it")?;
            run_shell_command(command)
        }
        UpgradeMethod::Script => {
            let script = status.script.as_deref().context("no script was provided for this application")?;
            let run = prepare_script_run(status, identity)?;
            let args: Vec<&str> = run.args.iter().map(String::as_str).collect();
            run_script(&status.application_name, script, &args, &run.env, run.timeout).map(|_| ())
        }
        other => anyhow::bail!("no runnable upgrade action for method {other:?}"),
    }
}

/// How one `Script` row is to be run: its arguments, its extra environment and its time limit.
struct ScriptRun {
    args: Vec<String>,
    env: Vec<(String, String)>,
    timeout: Option<Duration>,
}

/// Everything `patch_one` decides before a `Script` row runs, split out so it can be tested without
/// staging and running a script: the signature checks, the argument checks, and which mode.
///
/// An action row is verified again in full here — action, every secret, and the script once more —
/// for the same reason the script signature is re-checked at all. It runs `--install` or
/// `--uninstall` in place of `--update`, receives its secrets as environment variables, and is
/// killed at `ACTION_TIMEOUT`; an upgrade row gets none of those, exactly as before. Any action
/// value at all takes the action branch, so a row this build does not understand is refused rather
/// than run as an upgrade. Kept identical in the other two agents.
fn prepare_script_run(status: &UpgradeStatus, identity: &AgentIdentity) -> Result<ScriptRun> {
    let script = status.script.as_deref().context("no script was provided for this application")?;
    let signature = status
        .script_signature
        .as_deref()
        .context("no signature was provided for this script — refusing to run it")?;
    identity::verify_artifact_signature(identity, script, signature)
        .context("script signature verification failed — refusing to run it")?;
    let app_id = status
        .application_identifier
        .as_deref()
        .context("no bundle identifier known, but the script requires --appId")?;
    reject_unsafe_script_argument("--appName", &status.application_name)?;
    reject_unsafe_script_argument("--appId", app_id)?;

    let (mode, env, timeout) = if status.is_action() {
        let action = verify_action(status, identity)?;
        (action.mode_flag(), secret_env(status), Some(ACTION_TIMEOUT))
    } else {
        ("--update", Vec::new(), None)
    };

    let args = ["--appName", status.application_name.as_str(), "--appId", app_id, mode].map(str::to_string).to_vec();
    Ok(ScriptRun { args, env, timeout })
}

/// The two fixed prefixes Homebrew installs into — `/opt/homebrew` on Apple Silicon, `/usr/local`
/// on Intel. The same pair `system_info::find_brew_binary` probes, and for the same reason.
const HOMEBREW_BIN_DIRS: [&str; 2] = ["/opt/homebrew/bin", "/usr/local/bin"];

/// What launchd hands a job when nothing sets `PATH` — used only if the inherited value is missing
/// or empty, so a script still finds the system tools it can normally take for granted.
const LAUNCHD_DEFAULT_PATH: &str = "/usr/bin:/bin:/usr/sbin:/sbin";

/// `PATH` for everything this module spawns.
///
/// Both entry points below run under launchd, which gives a job the bare
/// `/usr/bin:/bin:/usr/sbin:/sbin` and nothing of the logged-in user's shell configuration — so
/// `brew` is invisible on `PATH` even on a Mac that plainly has it, since neither of Homebrew's
/// prefixes is on that list. That is exactly why `system_info::find_brew_binary` locates the
/// binary by hand for the inventory scan; the upgrade path never got the same treatment, so the
/// Homebrew upgrade script's own `command -v brew` guard (see the server's
/// `HomebrewUpgradeScript.Build`) reported "homebrew is not installed" and *every*
/// Homebrew-managed application failed to patch, on every Mac in the fleet.
///
/// Prepended rather than substituted: an AI-authored `--update` script is told it may use whatever
/// the platform provides, so it must still see everything launchd offered. The other two agents
/// need no equivalent — a SYSTEM service inherits the machine-wide `PATH` both winget and
/// Chocolatey install themselves onto (see the Windows agent's
/// `system_info::scan_package_managers`), and systemd's compiled-in default already covers
/// `/usr/local/bin` (see the Linux agent's `system_info::find_binary`).
fn spawn_path() -> String {
    let inherited = std::env::var("PATH").unwrap_or_default();
    prepend_homebrew_prefixes(&inherited)
}

fn prepend_homebrew_prefixes(inherited: &str) -> String {
    let inherited = if inherited.is_empty() { LAUNCHD_DEFAULT_PATH } else { inherited };

    // Skipping the entries already there keeps a PATH that happens to name a Homebrew prefix from
    // growing a duplicate every time this runs.
    HOMEBREW_BIN_DIRS
        .into_iter()
        .chain(
            inherited
                .split(':')
                .filter(|dir| !dir.is_empty() && !HOMEBREW_BIN_DIRS.contains(dir)),
        )
        .collect::<Vec<_>>()
        .join(":")
}

/// The two values a signed script receives on its command line come from the server, and the
/// signature does not cover them — it covers the script text alone. Every approved script
/// interpolates `--appName` into a path (`/Applications/<appName>.app`, `%ProgramFiles%\<appName>`),
/// so a value carrying a separator or a dot entry would walk that path somewhere else through a
/// script that verifies perfectly. A compromised server could already do worse by other routes;
/// this closes the one that turns a *legitimately* signed script against the host it runs on, and
/// it costs nothing, because no real application name or identifier contains any of these.
/// Mirrored in all three agents.
fn reject_unsafe_script_argument(flag: &str, value: &str) -> Result<()> {
    let unsafe_char = |c: char| c == '/' || c == '\\' || c.is_control();
    if value.is_empty() || value == "." || value == ".." || value.chars().any(unsafe_char) {
        anyhow::bail!(
            "refusing to pass {flag} {value:?} to the upgrade script: it is empty, a dot entry, or contains a path separator or control character"
        );
    }
    Ok(())
}

fn run_shell_command(command: &str) -> Result<()> {
    logging::info(&format!("running command: sh -c {command:?}"));

    let output = Command::new("sh")
        .arg("-c")
        .arg(command)
        // A legacy PackageManagerCommand row is typically a bare `brew upgrade <formula>`, which
        // launchd's own PATH cannot resolve — see spawn_path.
        .env("PATH", spawn_path())
        .output()
        .context("failed to run command")?;

    log_output(
        "command",
        command,
        &output.status.to_string(),
        &String::from_utf8_lossy(&output.stdout),
        &String::from_utf8_lossy(&output.stderr),
    );

    if !output.status.success() {
        anyhow::bail!(
            "exited with {}: {}",
            output.status,
            String::from_utf8_lossy(&output.stderr).trim()
        );
    }

    Ok(())
}

/// Logs the exit status and full stdout/stderr of a completed command/script — regardless of
/// whether it succeeded, since even a "successful" run's output can matter for diagnosing
/// something that looked fine but wasn't, and a failure's own error message otherwise only ever
/// carried a truncated last line of stderr, not the full picture. Each stream is capped
/// defensively (a runaway script printing megabytes shouldn't be able to blow out the log file)
/// rather than left unbounded.
const MAX_LOGGED_OUTPUT_BYTES: usize = 8000;

/// Takes the streams as text rather than an `Output`, because an action row's have already been
/// through `redact` by the time they get here — and must have been, since this is the agent log.
fn log_output(kind: &str, invocation: &str, status: &str, stdout: &str, stderr: &str) {
    let stdout = truncate_for_log(stdout);
    let stderr = truncate_for_log(stderr);
    logging::info(&format!("{kind} finished (exit {status}): {invocation}\n--- stdout ---\n{stdout}\n--- stderr ---\n{stderr}"));
}

fn truncate_for_log(text: &str) -> String {
    let trimmed = text.trim();
    if trimmed.len() <= MAX_LOGGED_OUTPUT_BYTES {
        return trimmed.to_string();
    }

    // Back up to the nearest char boundary — a plain byte-index slice could otherwise land
    // inside a multi-byte UTF-8 character and panic.
    let mut cut = MAX_LOGGED_OUTPUT_BYTES;
    while !trimmed.is_char_boundary(cut) {
        cut -= 1;
    }

    format!("{}\n... [truncated, {} more byte(s)]", &trimmed[..cut], trimmed.len() - cut)
}

/// Where a script is staged before it runs — and it depends on who is running it.
///
/// The root daemon stages under its own `/Library/Application Support/kintsugi-agent/scripts`:
/// the parent is `root:wheel 0755`, so nobody else can create an entry in it, and this directory
/// is made `0700`. The per-user process, which still runs Homebrew rows itself, stages under its
/// own `user_state_dir`. Neither is `/tmp`, which is where every script used to go: a root process
/// writing a semi-predictable name into a world-writable directory with `fs::write` — which
/// follows a symlink — lets any local user choose where root writes, and then what root runs.
/// That was tolerable while these scripts ran as the logged-in user, and has not been since
/// `runs_as_root` moved AI-researched rows to the daemon. The Linux agent's `script_staging_dir`
/// made the same decision first; the Windows agent stages in SYSTEM's own `%TEMP%`, where creating
/// a symlink needs a privilege ordinary users lack.
fn script_staging_dir() -> Result<PathBuf> {
    // SAFETY: `geteuid` cannot fail and touches nothing.
    if unsafe { libc::geteuid() } == 0 {
        Ok(config::config_dir().join("scripts"))
    } else {
        Ok(config::user_state_dir()?.join("scripts"))
    }
}

/// Writes `script` to a private file, runs it with `args`, and removes it afterward regardless of
/// outcome — an AI-generated script left lying around is unnecessary exposure once it's done
/// running. Returns captured stdout on success (the caller trims it, since `--update-version` is
/// specified to print only the bare version string).
///
/// `env` is extra environment for the script — an action row's `KINTSUGI_SECRET_*` variables, and
/// nothing for an upgrade. Every value in it is `redact`ed out of the output before the output is
/// logged, put into an error, or returned. `timeout` kills the script once it has run that long
/// (see `run_bounded`); `None` lets it run to completion as upgrades always have.
fn run_script(application_name: &str, script: &str, args: &[&str], env: &[(String, String)], timeout: Option<Duration>) -> Result<String> {
    let timestamp = SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .unwrap_or_default()
        .as_secs();
    let sanitized_name: String = application_name
        .chars()
        .filter(|c| c.is_ascii_alphanumeric())
        .collect();

    let staging_dir = script_staging_dir()?;
    fs::create_dir_all(&staging_dir).with_context(|| format!("failed to create the script staging directory {}", staging_dir.display()))?;
    fs::set_permissions(&staging_dir, fs::Permissions::from_mode(0o700)).context("failed to lock down the script staging directory")?;

    let script_path = staging_dir.join(format!("kintsugi-upgrade-{sanitized_name}-{timestamp}.sh"));

    // `create_new` and O_NOFOLLOW on top of the private directory: they cost nothing, and they
    // make a name that is somehow already there — a symlink above all — an error rather than
    // something to write through. Mode set on the descriptor, so the umask cannot narrow it.
    let mut file = fs::OpenOptions::new()
        .write(true)
        .create_new(true)
        .mode(0o700)
        .custom_flags(libc::O_NOFOLLOW)
        .open(&script_path)
        .with_context(|| format!("failed to create the script's staging file {}", script_path.display()))?;
    file.write_all(script.as_bytes()).context("failed to write the script to its staging file")?;
    file.set_permissions(fs::Permissions::from_mode(0o700)).context("failed to make the script executable")?;
    drop(file);

    let invocation = format!("{} {}", script_path.display(), args.join(" "));
    logging::info(&format!("running script for {application_name}: {invocation}"));

    let mut command = Command::new(&script_path);
    command
        .args(args)
        // Homebrew's prefixes are not on the PATH launchd hands this process, and a `--update`
        // script reaching for `brew` (or anything else Homebrew provides) has no other way to
        // find it — see spawn_path.
        .env("PATH", spawn_path())
        .envs(env.iter().map(|(key, value)| (key, value)));
    let result = run_bounded(command, timeout);

    let _ = fs::remove_file(&script_path);

    finish_script(application_name, &invocation, result?, env, timeout)
}

/// The half of `run_script` after the script has stopped: redact, log, and turn the outcome into
/// the result. Split out so the redaction and the timeout's wording are testable without staging
/// a script. Kept identical in the other two agents.
fn finish_script(
    application_name: &str,
    invocation: &str,
    finished: Finished,
    env: &[(String, String)],
    timeout: Option<Duration>,
) -> Result<String> {
    // Before anything else reads them — the log line, the error text and the failure report all
    // come from these two strings.
    let stdout = redact(&String::from_utf8_lossy(&finished.stdout), env);
    let stderr = redact(&String::from_utf8_lossy(&finished.stderr), env);

    let Some(status) = finished.status else {
        let minutes = timeout.map_or(0, |timeout| timeout.as_secs() / 60);
        log_output(&format!("script for {application_name}"), invocation, "killed at the timeout", &stdout, &stderr);
        anyhow::bail!("killed after running for {minutes} minutes without finishing: {}", stderr.trim());
    };

    log_output(&format!("script for {application_name}"), invocation, &status.to_string(), &stdout, &stderr);

    if !status.success() {
        anyhow::bail!("exited with {status}: {}", stderr.trim());
    }

    Ok(stdout)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn a_script_argument_that_could_be_a_path_is_refused() {
        for value in ["../Other", "..", ".", "Foo/Bar", "Foo\\Bar", "", "Foo\nBar", "/Applications/Foo"] {
            assert!(reject_unsafe_script_argument("--appName", value).is_err(), "{value:?} should have been refused");
        }
    }

    #[test]
    fn an_ordinary_application_name_or_identifier_passes() {
        for value in ["Visual Studio Code", "Adobe Acrobat (64-bit)", "com.electron.ollama", "temurin-26", "Calibre Agent", "net.java.openjdk.jdk"] {
            assert!(reject_unsafe_script_argument("--appName", value).is_ok(), "{value:?} should have been accepted");
        }
    }

    fn status(method: UpgradeMethod, package_manager: Option<&str>) -> UpgradeStatus {
        UpgradeStatus {
            application_name: "Ollama".to_string(),
            installed_version: "0.32.14".to_string(),
            latest_version: Some("0.33.3".to_string()),
            update_available: true,
            method,
            application_identifier: Some("com.electron.ollama".to_string()),
            command: None,
            notes: None,
            script: Some("#!/bin/bash\n".to_string()),
            script_signature: None,
            command_signature: None,
            package_manager: package_manager.map(str::to_string),
            action: None,
            self_service: false,
            action_signature: None,
            secrets: Vec::new(),
        }
    }

    /// Never `/tmp`, whoever is running: the daemon's scripts run as root, and a world-writable
    /// staging directory under a predictable name is a local user's way of choosing what root runs.
    #[test]
    fn scripts_are_staged_under_a_directory_only_this_process_can_write() {
        let dir = script_staging_dir().unwrap();

        assert!(!dir.starts_with(std::env::temp_dir()), "{} is under the shared temp directory", dir.display());
        // SAFETY: `geteuid` cannot fail and touches nothing — the same call `script_staging_dir` makes.
        let expected_parent = if unsafe { libc::geteuid() } == 0 { config::config_dir() } else { config::user_state_dir().unwrap() };
        assert_eq!(dir, expected_parent.join("scripts"));
    }

    #[test]
    fn a_short_failure_is_reported_whole() {
        assert_eq!(truncate_for_report("  exited with 1: Permission denied  "), "exited with 1: Permission denied");
    }

    /// The tail, not the head: a script's last words are where its failure is, while its first are
    /// the same preamble every run prints.
    #[test]
    fn a_long_failure_keeps_its_end_rather_than_its_beginning() {
        let text = format!("{}THE ACTUAL ERROR", "preamble ".repeat(MAX_REPORTED_FAILURE_BYTES));

        let reported = truncate_for_report(&text);

        assert!(reported.ends_with("THE ACTUAL ERROR"));
        assert!(reported.starts_with("... [truncated,"));
    }

    /// The server rejects anything over ReportPatchFailureCommandValidator.MaxDetailsLength, and a
    /// rejected report loses the failure silently — so what is sent has to stay under it however
    /// much a script printed.
    #[test]
    fn a_reported_failure_never_exceeds_the_limit_by_more_than_its_own_notice() {
        let text = "x".repeat(MAX_REPORTED_FAILURE_BYTES * 10);

        let reported = truncate_for_report(&text);

        assert!(reported.len() < MAX_REPORTED_FAILURE_BYTES + 100, "was {} bytes", reported.len());
    }

    /// A byte-index slice landing inside a multi-byte character panics, which would take the whole
    /// patch cycle down while reporting that something else had already gone wrong.
    #[test]
    fn truncating_never_splits_a_multi_byte_character() {
        // 3 bytes each, so the cut point lands mid-character for two thirds of possible lengths.
        let text = "\u{2014}".repeat(MAX_REPORTED_FAILURE_BYTES);

        let reported = truncate_for_report(&text);

        assert!(reported.ends_with('\u{2014}'));
    }

    #[test]
    fn an_ai_researched_script_runs_as_root() {
        // The Ollama case: a root-owned /Applications bundle the logged-in user cannot replace.
        assert!(runs_as_root(&status(UpgradeMethod::Script, None)));
    }

    #[test]
    fn a_homebrew_script_stays_with_the_logged_in_user() {
        // Homebrew refuses to run as root, so this must never reach the daemon.
        assert!(!runs_as_root(&status(UpgradeMethod::Script, Some("Homebrew"))));
    }

    #[test]
    fn homebrews_own_row_stays_with_the_logged_in_user() {
        // The manager's own row is reported with no package manager (it *is* the manager — see
        // `system_info::scan_homebrew`), so on that field alone it looks AI-researched. It was
        // queued to the daemon, which refused it, and every cycle logged "failed to patch
        // Homebrew". Matched by name, case-insensitively like every other row name.
        let mut homebrew = status(UpgradeMethod::Script, None);
        homebrew.application_name = "Homebrew".to_string();
        homebrew.application_identifier = Some("brew".to_string());
        assert!(!runs_as_root(&homebrew));

        homebrew.application_name = "homebrew".to_string();
        assert!(!runs_as_root(&homebrew));
    }

    #[test]
    fn an_app_store_script_runs_as_root() {
        // The install half of a store update needs root and the download half needs the console
        // user's session; only root can be both (via `launchctl asuser`), so this must reach the
        // daemon. Matched case-insensitively like every other row name.
        assert!(runs_as_root(&status(UpgradeMethod::Script, Some("App Store"))));
        assert!(runs_as_root(&status(UpgradeMethod::Script, Some("app store"))));
    }

    #[test]
    fn every_action_row_runs_as_root_homebrews_included() {
        // Install and uninstall scripts are written for root by contract, and the Homebrew builder
        // drops to the prefix owner itself — see `runs_as_root`.
        let mut homebrew_install = status(UpgradeMethod::Script, Some("Homebrew"));
        homebrew_install.action = Some("Install".to_string());
        assert!(runs_as_root(&homebrew_install));
    }

    #[test]
    fn a_legacy_package_manager_command_stays_with_the_logged_in_user() {
        // A bare `brew upgrade <formula>`, whatever the server knows about its owner.
        assert!(!runs_as_root(&status(UpgradeMethod::PackageManagerCommand, None)));
    }

    #[test]
    fn a_status_without_the_package_manager_field_deserializes_as_standalone() {
        // A server older than the field: the other agents' structs ignore unknown fields the same
        // way, and an absent Option is None under serde without any attribute.
        let json = r#"{"applicationName":"Ollama","installedVersion":"1","latestVersion":null,"updateAvailable":true,"method":"Script","applicationIdentifier":null,"command":null,"notes":null,"script":null,"scriptSignature":null,"commandSignature":null}"#;

        let status: UpgradeStatus = serde_json::from_str(json).unwrap();

        assert_eq!(status.package_manager, None);
    }

    #[test]
    fn prepend_homebrew_prefixes_puts_both_prefixes_ahead_of_launchds_own_path() {
        // The exact value `launchctl print gui/<uid>/au.com.sharpblue.kintsugiagent-ui` reports as
        // this job's default environment, which is what made the Homebrew script's `command -v
        // brew` guard fail on a Mac with Homebrew installed.
        let path = prepend_homebrew_prefixes("/usr/bin:/bin:/usr/sbin:/sbin");

        assert_eq!(path, "/opt/homebrew/bin:/usr/local/bin:/usr/bin:/bin:/usr/sbin:/sbin");
    }

    #[test]
    fn prepend_homebrew_prefixes_keeps_everything_the_process_inherited() {
        let path = prepend_homebrew_prefixes("/usr/bin:/somewhere/custom");

        assert!(path.split(':').any(|dir| dir == "/somewhere/custom"));
    }

    #[test]
    fn prepend_homebrew_prefixes_does_not_duplicate_a_prefix_already_present() {
        // A PATH that already names a prefix (an interactive `brew`-configured shell, say) would
        // otherwise grow a duplicate entry on every call.
        let path = prepend_homebrew_prefixes("/usr/local/bin:/usr/bin:/opt/homebrew/bin");

        assert_eq!(path, "/opt/homebrew/bin:/usr/local/bin:/usr/bin");
    }

    #[test]
    fn prepend_homebrew_prefixes_falls_back_to_the_system_path_when_nothing_was_inherited() {
        // An empty result would leave a script unable to find even `/usr/bin/curl`, which is a
        // worse failure than the one being fixed.
        let path = prepend_homebrew_prefixes("");

        assert_eq!(path, "/opt/homebrew/bin:/usr/local/bin:/usr/bin:/bin:/usr/sbin:/sbin");
    }

    /// Software titles: action rows. A module of its own so its helpers cannot collide with the
    /// ones above. Kept identical in the other two agents, apart from the shell the bounded-run
    /// tests use.
    mod actions {
        use super::super::*;
        use base64::Engine;
        use p256::ecdsa::signature::Signer;
        use p256::ecdsa::{Signature, SigningKey};
        use p256::pkcs8::EncodePublicKey;

        fn signing_key() -> SigningKey {
            SigningKey::from_bytes(&[0x42; 32].into()).expect("32 fixed bytes is a valid P-256 scalar")
        }

        fn test_identity() -> AgentIdentity {
            AgentIdentity {
                certificate_pem: String::new(),
                private_key_pem: String::new(),
                artifact_signing_public_key_pem: signing_key().verifying_key().to_public_key_pem(Default::default()).unwrap(),
            }
        }

        fn sign(content: &str) -> String {
            let signature: Signature = signing_key().sign(content.as_bytes());
            base64::engine::general_purpose::STANDARD.encode(signature.to_der().as_bytes())
        }

        fn signed_secret(name: &str, value: &str) -> ScriptSecret {
            ScriptSecret { name: name.to_string(), value: value.to_string(), signature: sign(&canonical_secret_text(name, value)) }
        }

        /// An action row exactly as the server would sign it: script, action and secrets all genuine.
        fn action_row(action: &str, secrets: Vec<ScriptSecret>) -> UpgradeStatus {
            let script = "#!/bin/bash\nexit 0\n".to_string();
            let names: Vec<&str> = secrets.iter().map(|secret| secret.name.as_str()).collect();
            let action_signature = sign(&canonical_action_text(action, Some("winget"), "Greenshot", "Greenshot.Greenshot", &names));
            let json = serde_json::json!({
                "applicationName": "Greenshot",
                "installedVersion": "",
                "latestVersion": "1.3.300",
                "updateAvailable": false,
                "method": "Script",
                "applicationIdentifier": "Greenshot.Greenshot",
                "command": null,
                "notes": null,
                "script": script,
                "scriptSignature": sign(&script),
                "commandSignature": null,
                "packageManager": "winget",
                "action": action,
                "selfService": false,
                "actionSignature": action_signature,
                "secrets": null,
            });
            let mut status: UpgradeStatus = serde_json::from_value(json).unwrap();
            status.secrets = secrets;
            status
        }

        /// The public half of the throwaway key the server's `SoftwareTitleTests` signed its vectors
        /// with (IP-880). Its private half was discarded and no deployment uses it.
        const SERVER_VECTOR_PUBLIC_KEY: &str = "-----BEGIN PUBLIC KEY-----
MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEi2WXbdjBuXiEu0Ocj0pY1DkLHpHn
83k5xJ3uZR0m6TdatTZOoNWv+u6xYbLv3pibsOAzMhP7SWKRIxTevVVhrA==
-----END PUBLIC KEY-----
";

        /// The contract's fixed vectors, byte for byte the strings the server's
        /// `SoftwareTitleTests` asserts — so the two ends cannot drift apart without one side's
        /// test failing. The first gives its secrets out of order, so the sort is pinned too.
        #[test]
        fn the_canonical_action_text_matches_the_servers_vectors() {
            assert_eq!(
                canonical_action_text("Install", Some("winget"), "Greenshot", "Greenshot.Greenshot", &["TEST_SECRET_B", "TEST_SECRET_A"]),
                "kintsugi-action-v1\nInstall\nwinget\nGreenshot\nGreenshot.Greenshot\nTEST_SECRET_A,TEST_SECRET_B"
            );
            assert_eq!(
                canonical_action_text("Uninstall", None, "Zoho Assist", "Zoho Assist", &[]),
                "kintsugi-action-v1\nUninstall\n\nZoho Assist\nZoho Assist\n"
            );
        }

        #[test]
        fn the_canonical_secret_text_matches_the_servers_vector() {
            assert_eq!(
                canonical_secret_text("TEST_SECRET_A", "TEST_SECRET_VALUE"),
                "kintsugi-secret-v1\nTEST_SECRET_A\nTEST_SECRET_VALUE"
            );
        }

        /// The signatures the server's own signing service made over those vectors verify here, with
        /// the texts this agent builds — the whole chain from C# to this agent, not just the strings.
        #[test]
        fn the_servers_signatures_over_the_vectors_verify_against_the_texts_built_here() {
            let identity = AgentIdentity {
                certificate_pem: String::new(),
                private_key_pem: String::new(),
                artifact_signing_public_key_pem: SERVER_VECTOR_PUBLIC_KEY.to_string(),
            };

            let install = canonical_action_text("Install", Some("winget"), "Greenshot", "Greenshot.Greenshot", &["TEST_SECRET_B", "TEST_SECRET_A"]);
            identity::verify_artifact_signature(
                &identity,
                &install,
                "MEUCIAOEin4HKT8ux3I4rc/8Zlf0XbJlGQbRUJoy68K2uz9kAiEAoiihP5CRKIAhsc8gvLtAv7gP+vDNWuVQHLgIgJ4Ze2w=",
            )
            .unwrap();

            let uninstall = canonical_action_text("Uninstall", None, "Zoho Assist", "Zoho Assist", &[]);
            identity::verify_artifact_signature(
                &identity,
                &uninstall,
                "MEUCIQDpXK5YI6DMxwSFLsUqxWvjJTO1H/xn7K4leXAIGaOeHwIgBm0PvP4CzDqaMnF0AcWZhlxDNUg89sTKjIri0jDM2WA=",
            )
            .unwrap();

            let secret = canonical_secret_text("TEST_SECRET_A", "TEST_SECRET_VALUE");
            identity::verify_artifact_signature(
                &identity,
                &secret,
                "MEUCIQC4UAG/hlCMj8tWdJzxlug6HNE0a6xg7cTMEJpOjaedhgIgP4KNUjiEaCTsyQd3IiMdc2QyebtbgUPi7pRxKdPhdic=",
            )
            .unwrap();

            // And one byte off is refused, so the test above cannot pass by accepting anything.
            assert!(identity::verify_artifact_signature(
                &identity,
                &canonical_secret_text("TEST_SECRET_A", "TEST_SECRET_VALUF"),
                "MEUCIQC4UAG/hlCMj8tWdJzxlug6HNE0a6xg7cTMEJpOjaedhgIgP4KNUjiEaCTsyQd3IiMdc2QyebtbgUPi7pRxKdPhdic=",
            )
            .is_err());
        }

        /// A server from before software titles: every new field absent, and the row parses exactly as
        /// it always did.
        #[test]
        fn a_row_without_the_new_fields_parses_as_an_ordinary_upgrade() {
            let json = r#"{"applicationName":"Firefox","installedVersion":"1","latestVersion":"2","updateAvailable":true,"method":"Script","applicationIdentifier":"org.mozilla.firefox","command":null,"notes":null,"script":null,"scriptSignature":null,"commandSignature":null}"#;

            let status: UpgradeStatus = serde_json::from_str(json).unwrap();

            assert_eq!(status.action, None);
            assert!(!status.self_service);
            assert_eq!(status.action_signature, None);
            assert!(status.secrets.is_empty());
            assert!(!status.is_action());
        }

        /// The server sends `secrets: null` on every ordinary row; `#[serde(default)]` alone rejects a
        /// present null, which would have failed the whole work list.
        #[test]
        fn a_null_secrets_list_reads_as_empty() {
            let json = r#"{"applicationName":"Firefox","installedVersion":"1","latestVersion":"2","updateAvailable":true,"method":"Script","applicationIdentifier":"x","command":null,"notes":null,"script":null,"scriptSignature":null,"commandSignature":null,"packageManager":null,"action":null,"selfService":false,"actionSignature":null,"secrets":null}"#;

            let status: UpgradeStatus = serde_json::from_str(json).unwrap();

            assert!(status.secrets.is_empty());
        }

        #[test]
        fn an_action_row_with_secrets_parses_in_full() {
            let json = r#"{"applicationName":"CrowdStrike","installedVersion":"","latestVersion":null,"updateAvailable":false,"method":"Script","applicationIdentifier":"CrowdStrike","command":null,"notes":null,"script":"x","scriptSignature":"s","commandSignature":null,"packageManager":null,"action":"Install","selfService":true,"actionSignature":"a","secrets":[{"name":"CID","value":"fake-value","signature":"v"}]}"#;

            let status: UpgradeStatus = serde_json::from_str(json).unwrap();

            assert_eq!(status.action.as_deref(), Some("Install"));
            assert!(status.self_service);
            assert_eq!(status.secrets.len(), 1);
            assert_eq!(status.secrets[0].name, "CID");
        }

        /// A value a newer server adds must not fail the list — and must not be run, as an upgrade or
        /// as anything else.
        #[test]
        fn an_unknown_action_is_parsed_and_then_ignored() {
            let mut status = action_row("Install", Vec::new());
            status.action = Some("Repair".to_string());
            let identity = test_identity();

            assert!(!is_actionable(&status, &identity));
            assert!(!is_patchable(&status, &identity));
            assert!(prepare_script_run(&status, &identity).is_err(), "an unknown action must never fall through to --update");
        }

        #[test]
        fn a_genuinely_signed_action_row_is_actionable_but_never_patchable() {
            let identity = test_identity();
            let status = action_row("Install", vec![signed_secret("CROWDSTRIKE_CID", "fake-cid-0000")]);

            assert!(is_actionable(&status, &identity));
            assert!(!is_patchable(&status, &identity), "updateAvailable is false, which is what keeps older agents away");
        }

        /// The action signature covers the id: a database edit pointing the signed winget script at a
        /// different package must be refused.
        #[test]
        fn an_action_row_whose_identifier_was_changed_after_signing_is_refused() {
            let identity = test_identity();
            let mut status = action_row("Install", Vec::new());
            status.application_identifier = Some("Evil.Package".to_string());

            assert!(!is_actionable(&status, &identity));
            assert!(prepare_script_run(&status, &identity).is_err());
        }

        #[test]
        fn an_action_row_whose_mode_was_flipped_after_signing_is_refused() {
            let identity = test_identity();
            let mut status = action_row("Install", Vec::new());
            status.action = Some("Uninstall".to_string());

            assert!(!is_actionable(&status, &identity));
        }

        /// The CrowdStrike CID case: a secret value edited straight into the database.
        #[test]
        fn a_secret_whose_value_was_changed_after_signing_is_refused() {
            let identity = test_identity();
            let mut status = action_row("Install", vec![signed_secret("CROWDSTRIKE_CID", "fake-cid-0000")]);
            status.secrets[0].value = "fake-cid-9999".to_string();

            assert!(!is_actionable(&status, &identity));
            assert!(prepare_script_run(&status, &identity).is_err());
        }

        #[test]
        fn a_secret_added_after_the_action_was_signed_is_refused() {
            let identity = test_identity();
            let mut status = action_row("Install", Vec::new());
            status.secrets.push(signed_secret("EXTRA", "fake"));

            assert!(!is_actionable(&status, &identity), "the action signature names the secrets it was signed with");
        }

        #[test]
        fn a_secret_name_that_could_not_be_an_environment_variable_is_refused() {
            assert!(is_valid_secret_name("CROWDSTRIKE_CID"));
            assert!(is_valid_secret_name("A"));
            for name in ["", "lower", "1ABC", "A=B", "A-B", "A B", "A\0B", &"A".repeat(65)] {
                assert!(!is_valid_secret_name(name), "{name:?} should have been refused");
            }

            let identity = test_identity();
            let status = action_row("Install", vec![signed_secret("bad=name", "fake")]);
            assert!(!is_actionable(&status, &identity));
        }

        #[test]
        fn an_action_row_runs_its_mode_flag_with_its_secrets_and_the_timeout() {
            let identity = test_identity();

            let install = prepare_script_run(&action_row("Install", vec![signed_secret("API_KEY", "fake-key")]), &identity).unwrap();
            assert_eq!(install.args, ["--appName", "Greenshot", "--appId", "Greenshot.Greenshot", "--install"]);
            assert_eq!(install.env, vec![("KINTSUGI_SECRET_API_KEY".to_string(), "fake-key".to_string())]);
            assert_eq!(install.timeout, Some(ACTION_TIMEOUT));

            let uninstall = prepare_script_run(&action_row("Uninstall", Vec::new()), &identity).unwrap();
            assert_eq!(uninstall.args.last().map(String::as_str), Some("--uninstall"));
            assert!(uninstall.env.is_empty());
        }

        #[test]
        fn an_upgrade_row_still_runs_update_with_no_secrets_and_no_timeout() {
            let identity = test_identity();
            let mut status = action_row("Install", vec![signed_secret("API_KEY", "fake-key")]);
            status.action = None;
            status.update_available = true;

            let run = prepare_script_run(&status, &identity).unwrap();

            assert_eq!(run.args.last().map(String::as_str), Some("--update"));
            assert!(run.env.is_empty(), "secrets are an action row's alone");
            assert_eq!(run.timeout, None);
        }

        #[test]
        fn the_privileged_half_finds_a_self_service_row_by_name() {
            let identity = test_identity();
            let mut offered = action_row("Install", Vec::new());
            offered.self_service = true;
            let statuses = vec![offered];

            assert!(find_runnable(&statuses, "greenshot", &identity).is_some(), "matched case-insensitively");
            assert!(find_runnable(&statuses, "Firefox", &identity).is_none());
        }

        #[test]
        fn a_success_without_a_known_version_still_reports_something_that_closes_the_failure() {
            let mut install = action_row("Install", Vec::new());
            install.latest_version = None;
            assert_eq!(reported_version(&install).as_deref(), Some("installed"));

            let mut uninstall = action_row("Uninstall", Vec::new());
            uninstall.latest_version = None;
            assert_eq!(reported_version(&uninstall).as_deref(), Some("removed"));

            assert_eq!(reported_version(&action_row("Install", Vec::new())).as_deref(), Some("1.3.300"));
        }

        #[test]
        fn a_secret_value_never_reaches_debug_output() {
            let status = action_row("Install", vec![signed_secret("API_KEY", "fake-value-do-not-print")]);

            let debug = format!("{status:?}");

            assert!(!debug.contains("fake-value-do-not-print"), "{debug}");
            assert!(debug.contains("API_KEY"));
        }

        #[test]
        fn redaction_replaces_every_value_with_its_name() {
            let env = vec![
                ("KINTSUGI_SECRET_SHORT".to_string(), "abc".to_string()),
                ("KINTSUGI_SECRET_LONG".to_string(), "abcdef".to_string()),
                ("KINTSUGI_SECRET_EMPTY".to_string(), String::new()),
            ];

            assert_eq!(redact("key=abcdef then abc", &env), "key=[secret LONG] then [secret SHORT]", "longest first, so no half-secret is left");
            assert_eq!(redact("nothing here", &env), "nothing here", "an empty value must not match everywhere");
        }

        fn shell(script: &str) -> Command {
            let mut command = Command::new("sh");
            command.arg("-c").arg(script);
            command
        }

        /// The environment reaches the script, and the value it prints comes back redacted — in the
        /// error text, which is what the Failed Updates screen is sent.
        #[test]
        fn a_secret_reaches_the_script_and_is_redacted_from_what_it_printed() {
            let env = vec![("KINTSUGI_SECRET_TOKEN".to_string(), "fake-token-123".to_string())];
            let mut command = shell(r#"echo "out:$KINTSUGI_SECRET_TOKEN"; echo "err:$KINTSUGI_SECRET_TOKEN" >&2; exit 3"#);
            command.envs(env.iter().map(|(key, value)| (key, value)));

            let finished = run_bounded(command, Some(Duration::from_secs(30))).unwrap();
            let error = finish_script("Test", "test", finished, &env, Some(Duration::from_secs(30))).unwrap_err().to_string();

            assert!(error.contains("err:[secret TOKEN]"), "{error}");
            assert!(!error.contains("fake-token-123"), "{error}");
        }

        #[test]
        fn a_successful_scripts_output_is_redacted_too() {
            let env = vec![("KINTSUGI_SECRET_TOKEN".to_string(), "fake-token-123".to_string())];
            let mut command = shell(r#"echo "$KINTSUGI_SECRET_TOKEN""#);
            command.envs(env.iter().map(|(key, value)| (key, value)));

            let finished = run_bounded(command, None).unwrap();
            let stdout = finish_script("Test", "test", finished, &env, None).unwrap();

            assert_eq!(stdout.trim(), "[secret TOKEN]");
        }

        /// A hung installer must not wedge the check-in: the script — and what it started — is killed
        /// at the bound and reported as a failure.
        #[test]
        fn a_script_that_outlives_its_timeout_is_killed_with_everything_it_started() {
            let started = Instant::now();

            // The background `sleep` is the installer the script started; it holds the output pipe, so
            // only killing the whole tree lets this return promptly.
            let finished = run_bounded(shell("sleep 60 & sleep 60"), Some(Duration::from_secs(1))).unwrap();

            assert!(finished.status.is_none(), "it should have been killed, not have exited");
            assert!(started.elapsed() < Duration::from_secs(1) + OUTPUT_GRACE + Duration::from_secs(3), "took {:?}", started.elapsed());
            let error = finish_script("Test", "test", finished, &[], Some(Duration::from_secs(60))).unwrap_err().to_string();
            assert!(error.starts_with("killed after running for 1 minutes"), "{error}");
        }

        #[test]
        fn a_script_that_finishes_in_time_is_left_alone() {
            let finished = run_bounded(shell("echo done"), Some(Duration::from_secs(30))).unwrap();

            assert!(finished.status.is_some_and(|status| status.success()));
            assert_eq!(String::from_utf8_lossy(&finished.stdout).trim(), "done");
        }
    }
}
