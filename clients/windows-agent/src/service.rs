use std::path::PathBuf;
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::Arc;
use std::time::{Duration, SystemTime, UNIX_EPOCH};

use anyhow::{Context, Result};
use serde::{Deserialize, Serialize};

use crate::baseline;
use crate::config::{self, Config};
use crate::forced_patch_run::{self, ForcedPatchRun};
use crate::health;
use crate::identity::{self, AgentIdentity};
use crate::log_collection;
use crate::logging;
use crate::os_update;
use crate::policy;
use crate::queue::{self, Plan, PlannedApp, RequestHandler};
use crate::running_apps;
use crate::self_removal;
use crate::self_update;
use crate::system_info::{self, InstalledApp};
use crate::update_state;
use crate::upgrade;
use crate::{checkin_schedule, GET_ATTEMPTS, GET_ATTEMPT_TIMEOUT, GET_RETRY_DELAY, MAX_ATTEMPTS, INITIAL_BACKOFF};

/// How often the service wakes to look at the queue when no check-in is due.
///
/// The macOS agent's daemon has no loop at all — launchd re-invokes it, and a `WatchPaths` entry on
/// the queue directory wakes it on demand. A Windows service is resident instead, so it owns both:
/// this is the queue's polling interval, kept short because a user is sitting in front of a progress
/// window waiting for the request to be answered.
const QUEUE_POLL_INTERVAL: Duration = Duration::from_secs(2);

/// What the service tells the SCM to expect between a stop request and `Stopped` — see the control
/// handler in `main`. `run_loop` notices the request within `QUEUE_POLL_INTERVAL` when idle, but a
/// stop that lands mid-check-in waits for the check-in (two POSTs with retries, a Windows Update
/// search, a package download), and one that lands mid-queue-request waits for that request, which
/// may be a Windows update install. Nothing that stops this service enforces the hint — the SCM only
/// does so at system shutdown, under its own `WaitToKillServiceTimeout` — so it is an honest
/// estimate for tools that display it, not a deadline.
pub const STOP_WAIT_HINT: Duration = Duration::from_secs(5 * 60);

/// How long a cached patching policy is trusted before the service bothers re-fetching it — the
/// policy changes rarely, so there's no need to hit the server on every check-in.
const POLICY_REFRESH_INTERVAL: u64 = 60 * 60;

/// How often the service asks for an administrator's "Patch now" itself, which it does only while
/// nobody is logged on (`Agent::run_forced_patch_runs_unattended`). The tray's own
/// `main::AGENT_POLL_INTERVAL`, so an empty desk starts as soon as an occupied one would.
const UNATTENDED_FORCED_POLL_INTERVAL: u64 = 60;

/// How long `run_loop` waits before retrying a check-in that failed, per consecutive failure. Once
/// these are spent it falls back to this host's hourly minute.
///
/// A check-in already retries internally (`MAX_ATTEMPTS` with `INITIAL_BACKOFF`), so this is
/// retrying a retry, and the intervals are sized accordingly: long enough that a host with a real
/// problem is not hammering a dead endpoint, short enough that a transient one recovers in minutes
/// rather than within the hour. The hour is the part that hurt. A policy refresh is the last step of
/// a check-in, so *any* earlier failure leaves the per-user tray process with no policy — and it
/// cannot fetch one itself (see `main::wait_for_policy`). A single bad network moment therefore used
/// to cost a full hour of a tray agent that looked uninstalled. That happened on a host dropping
/// about 40% of its connections: five internal attempts spread over two minutes all landed badly,
/// and nothing tried again until the next hour.
///
/// Bounded rather than open-ended deliberately. A host that cannot reach the server at all should
/// settle into the hourly rhythm and leave a log that can be read, not one line every few minutes
/// forever.
const CHECK_IN_RETRY_BACKOFF: [u64; 3] = [2 * 60, 5 * 60, 15 * 60];

/// How long to wait before the next check-in attempt given how many have failed in a row, or `None`
/// to fall back to this host's hourly minute. Its own function so the `wrapping_sub` is testable:
/// zero consecutive failures means the last one succeeded, and `0usize - 1` has to land outside the
/// array rather than at its end.
fn retry_delay(consecutive_failures: usize) -> Option<u64> {
    CHECK_IN_RETRY_BACKOFF.get(consecutive_failures.wrapping_sub(1)).copied()
}

/// Whether the service collects forced patch runs itself: only when nobody at all is logged on.
/// Anybody logged on, locked or disconnected included, has a tray polling for them, and the tray owns
/// the five-minute warning. Sessions that cannot be read count as somebody, as they do for a restart.
fn nobody_logged_on(sessions: &Result<Vec<crate::presence::LogonState>>) -> bool {
    matches!(sessions, Ok(sessions) if sessions.is_empty())
}

/// What an unattended forced run does with the names it collected: the tray's `run_forced` narrowing.
#[derive(Debug, Default, PartialEq, Eq)]
struct UnattendedRun {
    /// A run named `Windows` (`os_update::OS_FAILURE_APPLICATION_NAME`) installs the Windows update.
    os_update: bool,
    /// Named runs on the work list, spelled as the work list spells them, each once.
    apps: Vec<String>,
    /// Named runs with nothing to do here: current already, or no signed script.
    nothing_to_do: Vec<String>,
}

fn unattended_run(requested: &[ForcedPatchRun], patchable: &[String]) -> UnattendedRun {
    let mut run = UnattendedRun::default();
    for name in requested.iter().map(|forced| &forced.application_name) {
        if name.eq_ignore_ascii_case(os_update::OS_FAILURE_APPLICATION_NAME) {
            run.os_update = true;
        } else if let Some(app) = patchable.iter().find(|app| app.eq_ignore_ascii_case(name)) {
            if !run.apps.contains(app) {
                run.apps.push(app.clone());
            }
        } else {
            run.nothing_to_do.push(name.clone());
        }
    }
    run
}

#[derive(Debug, Serialize)]
struct RegisterHostRequest {
    hostname: String,
    #[serde(rename = "serialNumber")]
    serial_number: String,
    /// The minute-of-hour (0-59) this host currently checks in on — see `checkin_schedule`. Sent on
    /// every check-in so the server can track load per minute and, in its response, tell this host
    /// to move to a different one if its current minute is carrying more than its share.
    #[serde(rename = "checkInMinute")]
    check_in_minute: u8,
    #[serde(rename = "operatingSystem", skip_serializing_if = "Option::is_none")]
    operating_system: Option<String>,
    #[serde(rename = "ipAddress", skip_serializing_if = "Option::is_none")]
    ip_address: Option<String>,
    #[serde(rename = "operatingSystemUpdateAvailable", skip_serializing_if = "Option::is_none")]
    operating_system_update_available: Option<bool>,
    #[serde(rename = "operatingSystemLatestVersion", skip_serializing_if = "Option::is_none")]
    operating_system_latest_version: Option<String>,
    /// This build's own version, so the Hosts screen can show which agent release each host is
    /// running — mirrors `CreateHostCommand.AgentVersion`. Always sent: the agent always knows it.
    #[serde(rename = "agentVersion")]
    agent_version: &'static str,
    /// The build with its update revision, e.g. `"22631.4317"` — mirrors
    /// `CreateHostCommand.OperatingSystemVersionId`. Sent alongside `operating_system`, which
    /// carries only the bare build, because NVD matches Windows CVEs on the revision: without it
    /// the server refuses to assess this host's operating system rather than reporting every CVE
    /// fixed in any revision of the build. See `system_info::operating_system_build`.
    ///
    /// The `operatingSystemId` half of that pair is deliberately never sent from Windows: it is
    /// os-release's `ID`, which only Linux has, and `operating_system` already identifies the
    /// product unambiguously here.
    #[serde(rename = "operatingSystemVersionId", skip_serializing_if = "Option::is_none")]
    operating_system_version_id: Option<String>,
    /// Who is logged on — mirrors `CreateHostCommand.LoggedInUser`, so the Hosts screen can find
    /// this machine by its user. Omitted when nobody is, which the server reads as "keep the last
    /// one". See `presence::logged_on_users`.
    #[serde(rename = "loggedInUser", skip_serializing_if = "Option::is_none")]
    logged_in_user: Option<String>,
    /// Build, update revision, last scan and Windows Update policy — see `update_state`. Additive:
    /// an older server ignores it, and it is omitted when the read failed.
    #[serde(rename = "windowsUpdateState", skip_serializing_if = "Option::is_none")]
    windows_update_state: Option<update_state::UpdateState>,
}

/// Mirrors the backend's `CreateHostResult` — see
/// Kintsugi.Application/Hosts/Commands/CreateHost/CreateHostCommand.cs. Fields this agent has no use
/// for (host, wasCreated) stay omitted, the same way `self_update`'s `AgentPackageInfo` omits ones
/// it doesn't need.
#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
struct RegisterHostResponse {
    #[serde(default)]
    suggested_check_in_minute: Option<u8>,
    /// Set once an admin has requested this host be removed (see
    /// Kintsugi.Domain.Entities.Host.RemovalRequested) — tells this check-in to uninstall the agent
    /// completely instead of continuing on to application registration and everything else below
    /// it. See `self_removal::run`.
    #[serde(default)]
    removal_requested: bool,
}

#[derive(Debug, Serialize)]
struct RegisterApplicationsRequest {
    #[serde(rename = "serialNumber")]
    serial_number: String,
    applications: Vec<InstalledApp>,
}

/// What started a check-in — which decides whether it acts on software titles.
///
/// Only the hourly one in `run_loop` does (`upgrade::act_on_assigned`). A "Check In Now" runs the
/// same check-in from inside `process_queue`, so an install there would hold every other queued
/// request — Patch Now, a self-service click — behind it, while the tray waits on the check-in for
/// at most `checkin_schedule::CHECK_IN_TIMEOUT` and an install may take `upgrade::ACTION_TIMEOUT`.
/// `--check-in` from a shell counts as requested too: somebody is watching it. The Linux agent
/// passes the same flag; the macOS daemon tells the two apart by whether it served a request.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum CheckInTrigger {
    Timer,
    Requested,
}

/// Everything the service needs to answer a queue request or run a check-in, established once at
/// startup and reused for the life of the process.
pub struct Agent {
    config: Config,
    client: reqwest::blocking::Client,
    identity: Option<AgentIdentity>,
    serial_number: String,
    /// This host's assigned check-in minute and where it is persisted — owned here rather than by
    /// `run_loop` because a check-in can now also be asked for over the queue ("Check In Now"), and
    /// both callers have to apply the server's answer the same way. See `check_in`.
    schedule_path: PathBuf,
    check_in_minute: u8,
    /// Set once a check-in response has asked for removal, so the loop stops rather than carrying on
    /// against files it has just deleted.
    removed: bool,
    /// The updates Windows says never restart, from the last check-in's own search, for the silent pass
    /// that follows it — so the pass does not search Windows Update a second time each hour. Taken by
    /// the pass, and `None` when the search failed.
    no_restart_updates: Option<Vec<(String, String)>>,
}

impl Agent {
    pub fn new() -> Result<Self> {
        let config = Config::load();
        let serial_number = system_info::serial_number().context("could not determine this machine's identifier")?;

        let schedule_path = config::checkin_schedule_path();
        let check_in_minute = checkin_schedule::load_or_assign(&schedule_path);

        // Enrolls on first run; reuses the same identity from then on, until it needs replacing
        // (e.g. this host was decommissioned and re-provisioned). Every agent-only route is
        // rejected by nginx without the resulting certificate — see nginx/default.conf.
        let identity = identity::load_or_enroll(&config, &serial_number);
        let client = identity::build_client(Duration::from_secs(15), identity.as_ref()).context("failed to build HTTP client")?;

        Ok(Self { config, client, identity, serial_number, schedule_path, check_in_minute, removed: false, no_restart_updates: None })
    }

    pub fn serial_number(&self) -> &str {
        &self.serial_number
    }

    pub fn check_in_minute(&self) -> u8 {
        self.check_in_minute
    }

    /// Re-checks disk for an identity this service failed to enroll earlier.
    ///
    /// Enrollment can fail for entirely transient reasons — the machine booted before the network
    /// was up, or the configured token had just been rotated — and without this the service would
    /// stay unenrolled (and so unable to do anything at all) until someone restarted it.
    fn ensure_identity(&mut self) {
        if self.identity.is_some() {
            return;
        }

        self.identity = identity::load_or_enroll(&self.config, &self.serial_number);
        if self.identity.is_some() {
            match identity::build_client(Duration::from_secs(15), self.identity.as_ref()) {
                Ok(client) => {
                    self.client = client;
                    logging::info("agent identity established; resuming normal operation");
                }
                Err(err) => {
                    logging::error(&format!("enrolled an identity but could not build a client with it: {err:#}"));
                    self.identity = None;
                }
            }
        }
    }

    /// One full check-in: register this host and its installed applications, then check for a newer
    /// build of this agent itself, then adopt whichever check-in minute the server wants this host
    /// to use from now on.
    ///
    /// The same sequence the macOS daemon runs per invocation, minus the queue drain — a resident
    /// service handles the queue continuously in its own loop rather than once per check-in. Run on
    /// the hour by `run_loop`, once by `--check-in`, and on demand by a `RequestKind::CheckIn`.
    pub fn check_in(&mut self, trigger: CheckInTrigger) -> Result<()> {
        self.ensure_identity();

        let hostname = system_info::hostname().context("could not determine hostname")?;

        // Best-effort: registration still proceeds with just hostname + serial number if either of
        // these can't be determined.
        let operating_system = system_info::operating_system()
            .inspect_err(|err| logging::warn(&format!("could not determine operating system: {err}")))
            .ok();
        let ip_address = system_info::local_ip_address()
            .inspect_err(|err| logging::warn(&format!("could not determine local IP address: {err}")))
            .ok();

        // Best-effort, same as the OS name/version above: a host that can't run the standard update
        // check for some reason still gets registered, just without this piece reported.
        let os_update_status = os_update::check()
            .inspect_err(|err| logging::warn(&format!("could not check for Windows updates: {err}")))
            .ok();
        self.no_restart_updates = os_update_status.as_ref().map(|status| status.no_restart.clone());

        // Best-effort and read-only, like the update check above.
        let windows_update_state = update_state::collect(os_update_status.as_ref())
            .inspect_err(|err| logging::warn(&format!("could not read the Windows servicing state: {err:#}")))
            .ok();

        logging::info(&format!(
            "registering host: hostname={hostname} serial_number={} operating_system={operating_system:?} ip_address={ip_address:?} os_update_status={os_update_status:?}",
            self.serial_number
        ));

        let host_request = RegisterHostRequest {
            hostname,
            serial_number: self.serial_number.clone(),
            check_in_minute: self.check_in_minute,
            operating_system,
            ip_address,
            operating_system_update_available: os_update_status.as_ref().map(|s| s.available),
            operating_system_latest_version: os_update_status.and_then(|s| s.latest_version),
            agent_version: env!("CARGO_PKG_VERSION"),
            operating_system_version_id: system_info::operating_system_build(),
            logged_in_user: crate::presence::logged_on_users(),
            windows_update_state,
        };
        let host_response: RegisterHostResponse =
            post_with_retry(&self.client, &self.config.register_host_url(), &host_request).context("failed to register host")?;

        if host_response.removal_requested {
            logging::info("the server has marked this host for removal — uninstalling instead of continuing this check-in");
            self_removal::run(&self.client, &self.config, &self.serial_number);
            self.removed = true;
            return Ok(());
        }

        self.report_inventory()?;

        // Refreshed here rather than by the tray process, which has no identity of its own to fetch
        // it with — the cache file this writes is how the policy reaches it at all. See `queue`.
        let cache_path = config::policy_cache_path();
        let should_refresh = policy::load_cached(&cache_path).is_none_or(|cached| policy::is_stale(&cached, POLICY_REFRESH_INTERVAL));
        if should_refresh {
            policy::refresh(&self.client, &self.config, &cache_path);
        }

        // What this PC's own event log says about it shutting down by itself or logging hardware errors.
        // Best-effort and read-only: it never fails the check-in.
        health::report_new_events(&self.client, &self.config, &self.serial_number);

        // An administrator's request for this PC's event log, if one is waiting. Best-effort and
        // read-only: it never fails the check-in, and with nothing pending it is one cheap GET.
        log_collection::collect_and_upload_if_requested(&self.client, &self.config, &self.serial_number);
        // The device baselines assigned to this host, read and reported, and set back only where an
        // enforce item and a compiled-in write list both allow it — including each signed-in person's
        // managed Outlook signature, written as that person (`outlook_signature`). Best-effort like the
        // health report: it never fails the check-in.
        baseline::check_and_report(&self.client, &self.config, self.identity.as_ref(), &self.serial_number);

        // Another try at deleting a `.exe.old` the startup cleanup could not (an antivirus scanner
        // holding it open, say). Quiet when there is nothing to delete.
        self_update::retry_clean_up_previous_update();
        // The software-title pass: Required and Prohibited titles acted on silently, and the
        // self-service offers cached for the tray — see `upgrade::act_on_assigned`. The hourly
        // check-in only (see `CheckInTrigger`), and ahead of the agent's own update, so an install
        // is never interrupted by the service restarting underneath it.
        if trigger == CheckInTrigger::Timer {
            if let Some(identity) = self.identity.as_ref() {
                upgrade::act_on_assigned(&self.client, &self.config, &self.serial_number, identity, &config::self_service_path(), &mut || {
                    if let Err(err) = report_inventory(&self.client, &self.config, &self.serial_number) {
                        logging::warn(&format!("could not re-report the inventory after acting on software titles: {err:#}"));
                    }
                });
            }
        }

        // Last, and only after everything above has already succeeded: check whether a newer build
        // of this agent itself has been published, and install it in place if so — see
        // `self_update`. There's no policy/schedule governing the agent's own updates.
        self_update::check_and_apply(&self.client, &self.config, self.identity.as_ref(), env!("CARGO_PKG_VERSION"));

        // Applied last, matching the macOS ordering, so a minute change never lands halfway through
        // a check-in.
        let target_minute = host_response.suggested_check_in_minute.unwrap_or(self.check_in_minute);
        self.check_in_minute = checkin_schedule::apply(&self.schedule_path, self.check_in_minute, target_minute);

        Ok(())
    }

    /// The hourly check-in's silent pass: patches every pending application nobody has open, with
    /// nothing shown in any session, and leaves everything else for the tray's next due cycle and
    /// its prompt. Run by `run_loop` after each scheduled check-in — whether or not anybody is
    /// logged on, which is the one thing a Windows host does with nobody logged on (every other
    /// patch is started by the tray).
    ///
    /// What it does not do is as deliberate as what it does:
    ///
    /// - **Only the Windows updates that never restart** (`install_windows_updates_without_restart`),
    ///   each tried once. One that restarts, or only may, stays with the due cycle and its prompt. A
    ///   failure is logged, not reported: the due cycle installs the same update and reports it there.
    /// - **No schedule change.** The schedule is the tray's, and this patches what happened to be
    ///   shut, not the host; the next due cycle prompts for whatever is open.
    /// - **Failures are reported, not shown.** `patch_application` reports to the server exactly as
    ///   for a prompted cycle.
    ///
    /// Not part of `check_in` itself, because `check_in` also answers the tray's "Check In Now",
    /// whose asker gives up after `checkin_schedule::CHECK_IN_TIMEOUT` — and a pass over a dozen
    /// upgrades can take longer. `keep_going` is the service's shutdown flag: the queue is not
    /// drained while this runs, so a stop has to be noticed between applications rather than after
    /// them all. Kept identical in the other two agents — see "Patching what nobody has open" in
    /// `clients/CLAUDE.md`.
    pub fn patch_shut_applications_silently(&mut self, keep_going: &dyn Fn() -> bool) {
        if self.identity.is_none() {
            return;
        }
        // A self-update has just handed off to `--restart-service`, which is waiting for this
        // service to stop; patching now would hold that restart up for the length of the pass.
        if config::self_update_restart_marker_path().exists() {
            logging::info("skipping the check-in's silent pass: a self-update is waiting for this service to restart");
            return;
        }
        // First, because it needs no server: the applications below are skipped when it cannot be reached.
        if let Some(no_restart) = self.no_restart_updates.take() {
            if keep_going() {
                install_windows_updates_without_restart(&no_restart);
            }
        }
        let apps = match self.runnable() {
            // Upgrades only: a software title's install or removal is `act_on_assigned`'s, on the timer check-in.
            Ok(apps) => apps.into_iter().filter(|status| !status.is_action()).collect::<Vec<_>>(),
            Err(err) => {
                logging::warn(&format!("could not check for pending patches for the check-in's silent pass: {err:#}"));
                return;
            }
        };

        // A row already tried at this target is the due cycle's — see `running_apps::SilentAttempts`.
        let attempts_path = config::silent_attempts_path();
        let mut attempts = running_apps::SilentAttempts::load(&attempts_path);
        attempts.retain_pending(apps.iter().map(|app| (app.application_name.as_str(), app.latest_version.as_deref())));
        let now = now_epoch();
        let (tried, apps): (Vec<_>, Vec<_>) = apps
            .into_iter()
            .partition(|app| attempts.already_tried(&app.application_name, app.latest_version.as_deref(), app.run_signature(), now));
        if !tried.is_empty() {
            logging::info(&format!(
                "already tried by an earlier silent pass, so left for the next patch cycle: {}",
                tried.iter().map(|app| app.application_name.as_str()).collect::<Vec<_>>().join(", ")
            ));
        }

        let (shut, mut open) = running_apps::partition(apps, running_apps::is_open);
        for app in shut {
            if !keep_going() {
                break;
            }
            // Asked again now: an upgrade can take minutes, and the next application may have been
            // opened in the meantime.
            if running_apps::is_open(&app) != Some(false) {
                open.push(app);
                continue;
            }
            logging::info(&format!("{} is not open, so patching it without asking", app.application_name));
            attempts.record(&app.application_name, app.latest_version.as_deref(), app.run_signature(), now_epoch());
            attempts.save(&attempts_path);
            // `patch_application` re-fetches and re-verifies, and reports either outcome itself.
            if let Err(err) = RequestHandler::patch_application(self, &app.application_name) {
                logging::error(&format!("failed to patch {}: {err:#}", app.application_name));
            }
        }
        attempts.save(&attempts_path);
        if !open.is_empty() {
            logging::info(&format!(
                "left for the next patch cycle because they are open: {}",
                open.iter().map(|app| app.application_name.as_str()).collect::<Vec<_>>().join(", ")
            ));
        }
    }

    /// "Patch now" on a host nobody is logged on to, run by the service itself.
    ///
    /// The tray is what collects a forced run (`forced_patch_run::spawn_poller`), and a tray runs only
    /// in somebody's session, so a host nobody was logged on to kept the request for its 24 hours and
    /// let it expire. That is the host an administrator most needs it for: no scheduled cycle runs
    /// there either, because the schedule is the tray's too. This does what the tray's
    /// `patch_cycle::run_forced` does, from here, and only while there is no session for a tray to
    /// run in (`nobody_logged_on`):
    ///
    /// - **The same narrowing.** Only the named applications on this host's verified work list, less
    ///   self-service titles as the tray's `plan` leaves them out, and the Windows update only for a
    ///   run named `Windows`. `patch_application` re-fetches and re-verifies the script and reports
    ///   either outcome itself: an urgency override, never a trust override, as on the tray's path.
    /// - **No warning.** The five-minute "save your work" is for the person at the desk, and there is
    ///   nobody; the tray's own Windows run already skips it for a screen nobody would see.
    /// - **A restart only through `queue::restart_decision`**, with the sessions read again after the
    ///   install, since somebody may sign in during an install that can take an hour.
    /// - **No retry.** A failure is on Failed Updates like any other; `RetryList` is the tray's.
    ///
    /// Nothing is collected while a self-update waits for this service to restart, so the request is
    /// still on the server for the restarted one. `keep_going` is the shutdown flag, as for the silent
    /// pass; a stop between applications drops the rest, which the server has already marked collected.
    pub fn run_forced_patch_runs_unattended(&mut self, keep_going: &dyn Fn() -> bool) {
        if !nobody_logged_on(&crate::presence::logged_on_sessions()) || config::self_update_restart_marker_path().exists() {
            return;
        }
        let requested = match RequestHandler::forced_patch_runs(self) {
            Ok(requested) if requested.is_empty() => return,
            Ok(requested) => requested,
            Err(err) => {
                logging::warn(&format!("could not check for forced patch runs while nobody is logged on: {err:#}"));
                return;
            }
        };
        logging::info(&format!(
            "nobody is logged on, so the service runs this forced patch run itself, with no warning: {}",
            requested.iter().map(|run| run.application_name.as_str()).collect::<Vec<_>>().join(", ")
        ));

        let patchable = match self.runnable() {
            Ok(statuses) => statuses.into_iter().filter(|status| !status.self_service).map(|status| status.application_name).collect::<Vec<_>>(),
            Err(err) => {
                // As on the tray's path: the server has already marked these collected.
                logging::warn(&format!("could not check what to patch for a forced run, so it has been dropped: {err:#}"));
                return;
            }
        };
        let run = unattended_run(&requested, &patchable);
        if !run.nothing_to_do.is_empty() {
            logging::info(&format!("forced patch run has nothing to do for {}: nothing patchable matched", run.nothing_to_do.join(", ")));
        }
        for name in &run.apps {
            if !keep_going() {
                logging::warn(&format!("the service is stopping, so this forced patch run has been dropped from {name} on"));
                return;
            }
            if let Err(err) = RequestHandler::patch_application(self, name) {
                logging::error(&format!("failed to patch {name}: {err:#}"));
            }
        }
        if run.os_update && keep_going() {
            self.install_os_update_unattended();
        }
    }

    /// A forced run's Windows update with nobody logged on: the tray's `run_patches` Windows step and
    /// `request_restart`, without the notifications nobody would see.
    fn install_os_update_unattended(&mut self) {
        match os_update::check() {
            Ok(status) if status.available => {}
            Ok(_) => {
                logging::info("forced patch run for Windows has nothing to do: no Windows update is pending");
                return;
            }
            Err(err) => {
                logging::warn(&format!("could not check for Windows updates for a forced run: {err:#}"));
                return;
            }
        }
        // Reports either outcome to the server itself.
        let outcome = match RequestHandler::install_os_updates(self) {
            Ok(outcome) => outcome,
            Err(err) => {
                logging::error(&format!("could not install Windows updates for a forced run: {err:#}"));
                return;
            }
        };
        if !outcome.reboot_required {
            logging::info("Windows updates installed for a forced run; Windows needs no restart");
            return;
        }
        match queue::restart_decision(true, &crate::presence::logged_on_sessions()) {
            queue::RestartDecision::RestartIn(delay) => {
                logging::info(&format!("restarting in {delay}s to finish the forced run's Windows update"));
                if let Err(err) = os_update::restart(delay) {
                    logging::error(&format!("could not restart after the forced run's Windows update: {err:#}"));
                }
            }
            queue::RestartDecision::Defer(reason) => logging::info(&format!("not restarting after the forced run's Windows update: {reason}")),
        }
    }

    pub fn was_removed(&self) -> bool {
        self.removed
    }

    /// Scans this machine's applications and reports them — every check-in, and again straight
    /// after a software title was installed or removed (see `upgrade::act_on_assigned`).
    fn report_inventory(&self) -> Result<()> {
        report_inventory(&self.client, &self.config, &self.serial_number)
    }

    /// This host's upgrade statuses, filtered to what is actually runnable — the same
    /// `is_patchable` gate the macOS agent applies, run here rather than in the UI process because
    /// verifying a signature needs the pinned artifact-signing key, which lives with the identity.
    /// A software title's action row counts too when `is_actionable` says so, self-service ones
    /// included; `plan` leaves those out. `patch_application` asks `upgrade::find_runnable` instead,
    /// which applies the same two gates.
    fn runnable(&self) -> Result<Vec<upgrade::UpgradeStatus>> {
        let identity = self.identity.as_ref().context("this agent has not enrolled an identity yet")?;
        let statuses = upgrade::fetch_upgrade_statuses(&self.client, &self.config, &self.serial_number)?;
        Ok(statuses
            .into_iter()
            .filter(|status| if status.is_action() { upgrade::is_actionable(status, identity) } else { upgrade::is_patchable(status, identity) })
            .collect())
    }
}

/// The silent pass's Windows updates: those Windows says never restart, each tried once per
/// revision (`running_apps::SilentAttempts`, as for applications), so a failing one is left to the
/// due cycle rather than downloaded and failed again every hour. A Defender definitions update keeps
/// its id and moves its revision, so a new revision is a new attempt. Its own file, because the
/// application pass's `retain_pending` would forget these entries. `no_restart` is the check-in's own
/// search (`Agent::no_restart_updates`), not a new one.
fn install_windows_updates_without_restart(no_restart: &[(String, String)]) {
    let path = config::silent_os_attempts_path();
    let mut attempts = running_apps::SilentAttempts::load(&path);
    attempts.retain_pending(no_restart.iter().map(|(id, revision)| (id.as_str(), Some(revision.as_str()))));
    let untried: Vec<&str> = no_restart
        .iter()
        .filter(|(id, revision)| !attempts.already_tried(id, Some(revision.as_str()), None, now_epoch()))
        .map(|(id, _)| id.as_str())
        .collect();
    // Written before the attempt, as the application pass does, so one that never returns is not retried either.
    for (id, revision) in no_restart {
        attempts.record(id, Some(revision), None, now_epoch());
    }
    attempts.save(&path);
    if untried.is_empty() {
        return;
    }
    match os_update::install_without_restart(&untried) {
        Ok(outcome) if outcome.reboot_required => {
            logging::warn("a Windows update said it never restarts and then asked for one; the next prompt says a restart is pending")
        }
        Ok(_) => {}
        Err(err) => logging::warn(&format!("could not install the Windows updates that need no restart; the next patch cycle has them: {err:#}")),
    }
}

/// `Agent::report_inventory`, free of `&self` so the action pass's callback can call it while
/// `check_in` holds `self` borrowed.
fn report_inventory(client: &reqwest::blocking::Client, config: &Config, serial_number: &str) -> Result<()> {
    let applications = collect_installed_applications();
    logging::info(&format!("reporting {} installed application(s)", applications.len()));

    let applications_request = RegisterApplicationsRequest {
        serial_number: serial_number.to_string(),
        applications,
    };
    let _: serde_json::Value =
        post_with_retry(client, &config.register_applications_url(), &applications_request).context("failed to register installed applications")?;
    Ok(())
}

impl RequestHandler for Agent {
    fn plan(&mut self) -> Result<Plan> {
        self.ensure_identity();

        // A software title's actionable install or uninstall is included — not a self-service one,
        // which only the person at the keyboard asks for — so a forced run ("Run now") can find
        // it; the scheduled cycles drop it again with `Plan::without_actions`.
        let apps = self
            .runnable()?
            .into_iter()
            .filter(|status| !status.self_service)
            .map(|status| PlannedApp {
                open: running_apps::is_open(&status),
                application_name: status.application_name,
                latest_version: status.latest_version,
                action: status.action,
            })
            .collect();

        let os_update_available = os_update::check().map(|status| status.available).unwrap_or_else(|err| {
            logging::warn(&format!("could not check for Windows updates: {err:#}"));
            false
        });

        Ok(Plan { apps, os_update_available })
    }

    /// Asks the server what an administrator has forced against this host, and consumes it in the
    /// same call — see `forced_patch_run::collect`. The service is the half that can ask: the tray
    /// holds no mutual-TLS identity, so it relays this through the queue exactly as it does a plan.
    ///
    /// Nothing is *run* here. The answer is a list of names, and the tray turns it into an ordinary
    /// `AppPatch` request per application, which comes straight back to `patch_application` above —
    /// where the work list is re-fetched from the server and every signature re-verified. That is
    /// what keeps a forced run an urgency override rather than a trust override.
    fn forced_patch_runs(&mut self) -> Result<Vec<ForcedPatchRun>> {
        self.ensure_identity();

        // Empty rather than an error, and that distinction is the difference between a quiet log
        // and a line a minute. This is polled once per scheduler tick for the life of the tray
        // process, so a host that has not enrolled yet — or has just been un-enrolled — would
        // otherwise write a 403 into the log sixty times an hour saying nothing an administrator
        // can act on. There is genuinely nothing forced against a host the server does not know.
        if self.identity.is_none() {
            return Ok(Vec::new());
        }

        forced_patch_run::collect(&self.client, &self.config, &self.serial_number)
    }

    /// Runs one application's upgrade.
    ///
    /// The work list is re-fetched from the server here rather than trusted from the request, which
    /// is the property that makes the queue safe: the request named an application, and everything
    /// actually executed — the script, its signature, the identifier it's addressed by — comes from
    /// the server and is verified against the pinned artifact-signing key. A request that names an
    /// application with no signed, patchable upgrade path simply fails. See `queue`.
    ///
    /// A software title's action row is accepted as well as an upgrade, which is all "Run now" and
    /// a click in the "Install Software" submenu needed on this side: both arrive as this same
    /// request, and `upgrade::is_actionable` verifies the action and its secrets like the script.
    fn patch_application(&mut self, application_name: &str) -> Result<()> {
        self.ensure_identity();
        let identity = self.identity.as_ref().context("this agent has not enrolled an identity yet")?;

        let statuses = upgrade::fetch_upgrade_statuses(&self.client, &self.config, &self.serial_number)?;
        let status = upgrade::find_runnable(&statuses, application_name, identity)
            .cloned()
            .with_context(|| format!("'{application_name}' has no signed, patchable upgrade path or software title"))?;

        logging::info(&format!("attempting to patch {} (method {:?})", status.application_name, status.method));
        if let Err(err) = upgrade::patch_one(&status, identity) {
            // Reported from here rather than by the tray process, for the same reason the success
            // below is: this is the one place a script actually runs, and the only side holding the
            // identity every agent-only route requires. The tray merely relays the verdict through
            // `queue`, so a failure is recorded exactly once.
            //
            // Note what sits *above* this line and so is never reported: no enrolled identity, and
            // an application with no signed, patchable upgrade path. Those are configuration
            // problems rather than bugs in a script, and the Failed Updates screen exists for the
            // ones a human or the AI can fix.
            let outcome = Err(err);
            upgrade::report_outcome(&self.client, &self.config, &self.serial_number, &status, &outcome);
            return outcome;
        }
        logging::info(&format!("patched {} successfully", status.application_name));
        upgrade::report_outcome(&self.client, &self.config, &self.serial_number, &status, &Ok(()));

        // An install or an uninstall changes what is on this machine, so the server is told now
        // rather than at the next hourly check-in — the same as `upgrade::act_on_assigned` does.
        if status.is_action() {
            if let Err(err) = self.report_inventory() {
                logging::warn(&format!("could not re-report the inventory after {}: {err:#}", status.application_name));
            }
        }

        Ok(())
    }

    fn restart_host(&mut self, delay_seconds: u64) -> Result<()> {
        os_update::restart(delay_seconds)
    }

    /// Every session on the host, read from session 0 — the service is the one place that can see
    /// them all, which is why the restart decision is made here and not by the tray that asked.
    fn logged_on_sessions(&mut self) -> Result<Vec<crate::presence::LogonState>> {
        crate::presence::logged_on_sessions()
    }

    fn install_os_updates(&mut self) -> Result<os_update::InstallOutcome> {
        let outcome = match os_update::install() {
            Ok(outcome) => outcome,
            Err(err) => {
                // Reported from here, the one place the install runs, so the admin UI can say why a
                // host is not updating rather than showing it as merely waiting. See
                // `os_update::report_failed`.
                os_update::report_failed(&self.client, &self.config, &self.serial_number, &err);
                return Err(err);
            }
        };
        // Reported from here, not from the tray process, for the same reason the patch result above
        // is: this is the side holding the identity every agent-only route requires.
        os_update::report_patched(&self.client, &self.config, &self.serial_number);
        Ok(outcome)
    }

    /// Answers a [`queue::RequestKind::CheckIn`]: the same check-in `run_loop` runs on the hour,
    /// brought forward. `run_loop` leaves its own next wake-up where it was — an extra check-in at
    /// the hour costs two requests and nothing else, and the tray's "Next check-in" line predicts
    /// that hourly firing rather than this one.
    fn check_in(&mut self) -> Result<String> {
        Agent::check_in(self, CheckInTrigger::Requested)?;
        Ok(format!("checked in as {} (agent {})", self.serial_number, env!("CARGO_PKG_VERSION")))
    }
}

/// Combines the uninstall-registry scan with the package managers'. A winget- or Chocolatey-managed
/// application also has an uninstall-registry entry, so the registry scan is told which keys the
/// managers already account for (`managed_keys`) and skips those, keeping the manager-tagged entry
/// as the single source of truth for that application rather than also reporting it as a separate,
/// unmanaged one. Any remaining exact duplicates are still
/// deduplicated, since the backend rejects duplicate (host, name, version) rows in a single report.
///
/// Structurally identical to the macOS agent's `collect_installed_applications`, with winget and
/// Chocolatey in Homebrew's place.
pub fn collect_installed_applications() -> Vec<InstalledApp> {
    use std::collections::HashSet;

    let managers = system_info::scan_package_managers();

    let mut seen = HashSet::new();
    system_info::scan_installed_programs(&managers.managed_keys)
        .into_iter()
        .chain(managers.apps)
        .chain(system_info::scan_microsoft_store())
        .filter(|app| seen.insert(app.clone()))
        .collect()
}

/// A GET that survives the moment of packet loss a POST already survives.
///
/// `post_with_retry` has retried since this agent was written, so an intermittently lossy path to
/// the server is invisible on every POST — enrollment, inventory, patch results — and was fatal on
/// every GET, each of which had exactly one attempt. On one host that cost a whole patch cycle to a
/// bad minute: `upgrade::fetch_upgrade_statuses` is called once per application, deliberately, so
/// five applications in a row each spent the client's full timeout and reported "operation timed
/// out". The same link on the same evening is what `remote_control`'s session-socket retry exists
/// for; this is the third place the asymmetry showed up, and the two halves now sit together so a
/// fourth is harder to write.
///
/// The response is returned whatever its status. Every caller already tells a rejection apart from
/// a failure to reach the server, and a 4xx is not going to fix itself on retry.
pub fn get_with_retry(
    description: &str,
    build: impl Fn() -> reqwest::blocking::RequestBuilder,
) -> Result<reqwest::blocking::Response> {
    let mut last_error = None;

    for attempt in 1..=GET_ATTEMPTS {
        match build().timeout(GET_ATTEMPT_TIMEOUT).send() {
            Ok(response) => return Ok(response),
            Err(err) => {
                // {err:#} for the cause chain, the same reason post_with_retry does it.
                logging::warn(&format!("attempt {attempt}/{GET_ATTEMPTS} to fetch {description} failed: {err:#}"));
                last_error = Some(err);
            }
        }

        if attempt < GET_ATTEMPTS {
            std::thread::sleep(GET_RETRY_DELAY);
        }
    }

    Err(anyhow::anyhow!(
        "could not fetch {description} after {GET_ATTEMPTS} attempts: {}",
        last_error.map(|err| err.to_string()).unwrap_or_default()
    ))
}

pub fn post_with_retry<T: Serialize, R: serde::de::DeserializeOwned>(client: &reqwest::blocking::Client, url: &str, body: &T) -> Result<R> {
    let mut backoff = INITIAL_BACKOFF;
    let mut last_error = None;

    for attempt in 1..=MAX_ATTEMPTS {
        match client.post(url).json(body).send() {
            Ok(response) if response.status().is_success() => {
                let status = response.status();
                let body = response.text().unwrap_or_default();
                logging::info(&format!("POST {url} succeeded (HTTP {status}): {body}"));
                return serde_json::from_str(&body).context("could not parse response body");
            }
            Ok(response) => {
                let status = response.status();
                let body = response.text().unwrap_or_default();
                // A 4xx is not going to fix itself on retry (bad payload, validation failure); fail
                // fast instead of burning the retry budget.
                anyhow::bail!("request rejected (HTTP {status}): {body}");
            }
            Err(err) => {
                // {err:#}, not {err}: anyhow's plain Display prints only the outermost
                // message, and reqwest's outermost message for any connection failure is the
                // bare "error sending request for url (...)" — identical whether the host is
                // unreachable, the TLS handshake was rejected, or DNS failed. The cause chain is
                // where "invalid peer certificate: UnknownIssuer" lives, and without it a server
                // presenting an untrusted certificate is indistinguishable in this log from a
                // network outage. That cost real time to diagnose once; don't drop the `#`.
                logging::warn(&format!("attempt {attempt}/{MAX_ATTEMPTS} to {url} failed: {err:#}"));
                last_error = Some(err);
            }
        }

        if attempt < MAX_ATTEMPTS {
            std::thread::sleep(backoff);
            backoff *= 2;
        }
    }

    Err(anyhow::anyhow!(
        "failed after {MAX_ATTEMPTS} attempts: {}",
        last_error.map(|e| e.to_string()).unwrap_or_default()
    ))
}

fn now_epoch() -> u64 {
    SystemTime::now().duration_since(UNIX_EPOCH).unwrap_or_default().as_secs()
}

/// The service's whole working life: check in immediately, then alternate between serving the queue
/// and checking in once an hour at this host's own assigned minute.
///
/// `shutdown` is set by the Service Control Manager handler (see `windows_service`); every wait in
/// here is short enough that a stop request is honoured promptly rather than after an hour.
pub fn run_loop(shutdown: Arc<AtomicBool>) {
    logging::init(&config::service_log_path());

    // First, so a standard user can no longer plant a file the service reads in its state directory
    // (the restart marker self-update believes, say). Every start, for hosts install.ps1 never re-ran on.
    identity::restrict_state_directory_permissions(&config::config_dir());

    // Anything left from before a restart or a reboot has an owner that is long gone; acting on it
    // would start an unannounced patch cycle with nothing showing progress.
    queue::discard_stale(&config::queue_dir());
    // In memory only, so a service restart forgets any install that would have authorised a restart
    // — see `queue::RestartAuthorisation`.
    let mut restart_authorisation = queue::RestartAuthorisation::default();
    // The first moment nothing can still be running the copy a previous self-update displaced — and
    // the proof that the restart that update was waiting for has happened.
    self_update::clean_up_previous_update();

    let mut agent = match Agent::new() {
        Ok(agent) => agent,
        Err(err) => {
            // Nothing this service does is possible without an identifier for the host, and no
            // amount of retrying changes that — see `system_info::serial_number` for why this
            // refuses to invent one.
            logging::error(&format!("could not start the kintsugi-agent service: {err:#}"));
            return;
        }
    };

    logging::info(&format!(
        "kintsugi-agent service starting; api_base_url={} check_in_minute=:{:02}",
        Config::load().api_base_url,
        agent.check_in_minute()
    ));

    // Remote control gets a thread of its own, and it is the only standing outbound connection this
    // agent has. Everything else here is a request the service makes when it has something to say;
    // remote control is the one case where the *server* needs to reach the host, and an hourly
    // check-in cannot carry "somebody would like to see your screen now".
    //
    // Separate from this loop because it spends its life blocked on a pipe and then on sockets,
    // whereas this loop spends its life asleep on a two-second timer. Sharing one would mean a
    // session request waiting for the next tick, and a frame stream inside the queue's poll.
    {
        let remote_config = Config::load();
        let remote_serial = agent.serial_number().to_string();
        let remote_shutdown = shutdown.clone();
        std::thread::spawn(move || crate::remote_control::run(remote_config, remote_serial, remote_shutdown));
    }

    // Runs immediately at startup — the counterpart to the macOS LaunchDaemon's RunAtLoad, and what
    // makes a freshly installed agent appear in the fleet within seconds rather than within an hour.
    let mut next_check_in_at = now_epoch();
    let mut failures: usize = 0;
    // A minute after starting, as the tray's poller sleeps first.
    let mut next_forced_poll_at = now_epoch() + UNATTENDED_FORCED_POLL_INTERVAL;

    while !shutdown.load(Ordering::SeqCst) {
        if now_epoch() >= next_check_in_at {
            match agent.check_in(CheckInTrigger::Timer) {
                Ok(()) => failures = 0,
                Err(err) => {
                    failures += 1;
                    logging::warn(&format!("check-in failed ({failures}): {err:#}"));
                }
            }
            // Whether or not the check-in succeeded, as on Linux: the pass fetches its own work
            // list, and a server that cannot be reached simply leaves it nothing to do.
            if !agent.was_removed() {
                agent.patch_shut_applications_silently(&|| !shutdown.load(Ordering::SeqCst));
            }

            next_check_in_at = now_epoch()
                + match retry_delay(failures) {
                    // The tray's "Next check-in" line does not move for these: it is computed from
                    // the persisted hourly minute (see checkin_schedule::next_check_in_epoch), which
                    // stays true — a retry is an extra attempt, not a rescheduling. Writing retry
                    // times into that file would make the line flap and would give a file documented
                    // as carrying nothing but a minute a second meaning.
                    Some(backoff) => {
                        logging::info(&format!("retrying the check-in in {backoff}s rather than waiting for the next scheduled time"));
                        backoff
                    }
                    // Either it succeeded, or the short retries are spent and something is wrong
                    // that retrying every few minutes will not fix.
                    None => checkin_schedule::seconds_until(now_epoch(), agent.check_in_minute()),
                };
        }

        if now_epoch() >= next_forced_poll_at {
            if !agent.was_removed() {
                agent.run_forced_patch_runs_unattended(&|| !shutdown.load(Ordering::SeqCst));
            }
            next_forced_poll_at = now_epoch() + UNATTENDED_FORCED_POLL_INTERVAL;
        }

        queue::process_queue(&config::queue_dir(), &mut agent, &mut restart_authorisation);

        // After the queue rather than only after the hourly check-in: a queue-triggered check-in
        // (see `RequestHandler::check_in`) can learn of a removal too. A drain against the directory
        // `self_removal` has just deleted is a no-op, so nothing is lost by checking once, here.
        if agent.was_removed() {
            logging::info("this host has been removed; the service is stopping");
            return;
        }

        std::thread::sleep(QUEUE_POLL_INTERVAL);
    }

    logging::info("kintsugi-agent service stopping");
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn a_successful_check_in_waits_for_the_hourly_minute() {
        // Not the first backoff: `0 - 1` must fall outside the array rather than wrap onto its last
        // entry, or every success would schedule the next check-in 15 minutes out.
        assert_eq!(retry_delay(0), None);
    }

    #[test]
    fn consecutive_failures_back_off_and_then_stop_retrying() {
        assert_eq!(retry_delay(1), Some(2 * 60));
        assert_eq!(retry_delay(2), Some(5 * 60));
        assert_eq!(retry_delay(3), Some(15 * 60));
        // Bounded on purpose - see CHECK_IN_RETRY_BACKOFF. A host that cannot reach the server at
        // all settles into the hourly rhythm rather than logging every few minutes forever.
        assert_eq!(retry_delay(4), None);
        assert_eq!(retry_delay(400), None);
    }

    #[test]
    fn the_service_collects_forced_runs_only_when_nobody_at_all_is_logged_on() {
        use crate::presence::LogonState;
        assert!(nobody_logged_on(&Ok(vec![])));
        // Locked or disconnected is somebody: their tray polls, and owns the warning.
        assert!(!nobody_logged_on(&Ok(vec![LogonState::Away])));
        assert!(!nobody_logged_on(&Ok(vec![LogonState::AtTheDesk])));
        assert!(!nobody_logged_on(&Err(anyhow::anyhow!("WTSEnumerateSessionsW failed"))));
    }

    #[test]
    fn an_unattended_forced_run_is_narrowed_like_the_trays() {
        let forced = |name: &str| ForcedPatchRun { id: name.to_string(), application_name: name.to_string(), platform: "pm:winget".to_string() };
        let requested = [forced("libreoffice 7.6.7.2"), forced("WINDOWS"), forced("LibreOffice 7.6.7.2"), forced("Zoom")];
        let patchable = vec!["Google Chrome".to_string(), "LibreOffice 7.6.7.2".to_string()];

        assert_eq!(
            unattended_run(&requested, &patchable),
            UnattendedRun {
                os_update: true,
                apps: vec!["LibreOffice 7.6.7.2".to_string()],
                nothing_to_do: vec!["Zoom".to_string()],
            }
        );
        assert_eq!(unattended_run(&[], &patchable), UnattendedRun::default());
    }
}
