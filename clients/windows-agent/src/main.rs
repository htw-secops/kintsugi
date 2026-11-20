// A windowed (GUI-subsystem) program, so Windows never opens a console for it. The logon task starts
// `--agent` directly, and a console program started that way on Windows 11 opens in Windows
// Terminal, which `ShowWindow(GetConsoleWindow(), SW_HIDE)` cannot reach: users saw a terminal tab
// streaming this agent's log, and closing it killed the tray. The modes meant to be read by hand
// borrow their parent's console instead — see `attach_parent_console`. Not under `cargo test`,
// whose harness reports through the console.
#![cfg_attr(not(test), windows_subsystem = "windows")]

mod baseline;
mod branding;
mod chat_window;
mod checkin_schedule;
mod config;
mod dialogs;
mod forced_patch_run;
mod health;
mod identity;
mod input_injection;
mod log_collection;
mod logging;
mod os_update;
mod outlook_signature;
mod patch_cycle;
mod policy;
mod presence;
mod progress_window;
mod popup;
mod preview;
mod pty;
mod queue;
mod retry_state;
mod remote_control;
mod remote_desktop;
mod remote_ipc;
mod remote_protocol;
mod remote_session;
mod schedule;
mod screen_capture;
mod secure_attention;
mod self_removal;
mod self_service;
mod session_banner;
mod session_launcher;
mod self_update;
mod service;
mod status;
mod system_info;
mod tray_menu;
mod upgrade;
mod win32;

use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::{mpsc, Arc};
use std::time::Duration;

use anyhow::{Context, Result};

use schedule::ScheduleState;
use status::{AgentStatus, CheckInStatus, MenuAction, StatusReporter, StatusReporterFn};

/// How often the `--agent` loop wakes to check whether a patch cycle is due. Deliberately not tied
/// to the patching interval itself — this is just the scheduler's own tick rate, small enough that
/// a due time (or a delay elapsing, including one that elapsed while the PC was asleep — see
/// `ScheduleState::is_due`) is noticed promptly rather than up to a day late.
const AGENT_POLL_INTERVAL: Duration = Duration::from_secs(60);

/// The Service Control Manager restarts this service on its own if it fails; this bounded retry only
/// exists to ride out the short window at boot where the network isn't up yet.
pub const MAX_ATTEMPTS: u32 = 5;
pub const INITIAL_BACKOFF: Duration = Duration::from_secs(5);

/// A GET's own retry budget, which is deliberately not the POST one above.
///
/// A POST reports something that has already happened and can afford minutes of backoff; a GET is
/// holding up a patch cycle somebody may be watching. So this takes a per-attempt timeout well
/// under the client's own 15s and a short delay between attempts — a blackholed SYN is not answered
/// by waiting longer, it is answered by a fresh connection. See `get_with_retry`.
pub const GET_ATTEMPTS: u32 = 3;
pub const GET_ATTEMPT_TIMEOUT: Duration = Duration::from_secs(10);
pub const GET_RETRY_DELAY: Duration = Duration::from_millis(500);

fn main() -> Result<()> {
    // reqwest's rustls backend needs a process-wide default crypto provider installed before any
    // TLS connection is made; with exactly one provider feature compiled in (ring — see Cargo.toml)
    // higher-level callers usually do this themselves, but installing it explicitly, once, up front
    // removes any doubt — install_default() is a harmless no-op error (ignored here) if something
    // else already installed one first.
    let _ = rustls::crypto::ring::default_provider().install_default();

    // A panic in the scheduler thread would otherwise only ever reach the default panic hook's raw
    // stderr — which, for a service and for a windowless tray process alike, goes precisely nowhere.
    // Routing it through the same logger means a silent-looking failure (the scheduler thread dying,
    // with the menu just never updating again) always leaves a trace in the one file this agent's
    // own docs point people at first.
    std::panic::set_hook(Box::new(|info| logging::error(&format!("panic: {info}"))));

    let args: Vec<String> = std::env::args().collect();

    // The tray and the session helper never borrow a console: one attached to a terminal somebody
    // started it from would die with that terminal.
    if !args.iter().any(|arg| arg == "--agent" || arg == "--remote-session-helper") {
        attach_parent_console();
    }

    // The session helper, launched by the service as SYSTEM into the logged-in session — never run
    // by hand. Checked before --agent because it is the most privileged mode and the most specific.
    if args.iter().any(|arg| arg == "--remote-session-helper") {
        return run_remote_session_helper();
    }

    // The restart helper a self-update spawns from the newly installed binary — see
    // `self_update::restart_service` for why the service cannot restart itself inline.
    if args.iter().any(|arg| arg == self_update::RESTART_HELPER_ARGUMENT) {
        return run_service_restart_helper();
    }

    // Every popup, with example content, touching nothing — for judging how they look on a real
    // PC, with or without the agent installed. See `preview`.
    if args.iter().any(|arg| arg == "--preview-popups") {
        return preview::run(&args);
    }

    if args.iter().any(|arg| arg == "--agent") {
        return run_ui_agent();
    }

    if args.iter().any(|arg| arg == "--check-in") {
        return run_single_check_in();
    }

    // No arguments: started by the Service Control Manager.
    run_service()
}

/// Runs one check-in and exits — what `packaging/install.ps1` calls so a fresh install appears in
/// the fleet immediately, and the one mode that produces readable output when run by hand to
/// diagnose an enrollment problem.
fn run_single_check_in() -> Result<()> {
    logging::init(&config::service_log_path());

    service::Agent::new()?.check_in(service::CheckInTrigger::Requested)
}

/// Stops and starts the service, from outside it, on behalf of the process that just replaced its
/// own binary. Logs to the service's log, since the story it tells is the service's.
fn run_service_restart_helper() -> Result<()> {
    logging::init(&config::service_log_path());
    logging::info(&format!("kintsugi-agent ({}) starting", self_update::RESTART_HELPER_ARGUMENT));

    let result = self_update::run_restart_helper();
    if let Err(err) = &result {
        logging::error(&format!("the service restart helper failed: {err:#}"));
    }
    result
}

// ---------------------------------------------------------------------------------------------
// The service half — the counterpart to the macOS agent's root LaunchDaemon.
// ---------------------------------------------------------------------------------------------

windows_service::define_windows_service!(ffi_service_main, service_main);

fn run_service() -> Result<()> {
    // A failure here is almost always "this wasn't actually started by the SCM" — i.e. someone ran
    // the binary directly with no arguments — so the message names the modes that do work
    // interactively rather than reporting a bare OS error.
    windows_service::service_dispatcher::start(config::SERVICE_NAME, ffi_service_main).context(
        "could not connect to the Service Control Manager. This binary runs as a Windows service \
         with no arguments; to run it interactively use --check-in (one check-in) or --agent (the \
         per-user tray process)",
    )
}

fn service_main(_arguments: Vec<std::ffi::OsString>) {
    if let Err(err) = run_service_inner() {
        logging::error(&format!("the kintsugi-agent service stopped with an error: {err:#}"));
    }
}

fn run_service_inner() -> Result<()> {
    use std::sync::OnceLock;
    use windows_service::service::{ServiceControl, ServiceControlAccept, ServiceExitCode, ServiceState, ServiceStatus, ServiceType};
    use windows_service::service_control_handler::{self, ServiceControlHandlerResult, ServiceStatusHandle};

    let shutdown = Arc::new(AtomicBool::new(false));

    // The handler needs the status handle `register` is about to return, so it reads it through a
    // slot filled in immediately afterwards. A stop cannot arrive before that: the SCM refuses to
    // send controls the service has not yet declared it accepts, and that declaration is the
    // `Running` report below.
    let status_slot: Arc<OnceLock<ServiceStatusHandle>> = Arc::new(OnceLock::new());

    let handler_shutdown = Arc::clone(&shutdown);
    let handler_status = Arc::clone(&status_slot);
    let status_handle = service_control_handler::register(config::SERVICE_NAME, move |control| match control {
        // Interrogate must be answered for the SCM to consider the service responsive at all.
        ServiceControl::Interrogate => ServiceControlHandlerResult::NoError,
        // Shutdown (the machine is going down) is accepted alongside Stop so an in-flight check-in
        // is wound up rather than killed — during a patch cycle that matters, since the queue would
        // otherwise be left holding a half-answered request.
        //
        // Reporting StopPending here, rather than leaving the state at Running until `run_loop`
        // returns, is what makes that winding-up survive contact with the tools that stop this
        // service. `Stop-Service` — and so `Restart-Service`, which `install.ps1` and the older
        // self-update helper both relied on — waits two seconds for Stopped and then gives up unless
        // the service is reporting StopPending (PowerShell's `Service.cs`, `DoWaitForStatus`); the
        // loop below polls this flag every two seconds, and may be minutes into a check-in. Without
        // this the stop half of a restart timed out and the start half was never attempted, leaving
        // a freshly updated host running the old binary out of `kintsugi-agent.exe.old` for days.
        ServiceControl::Stop | ServiceControl::Shutdown => {
            handler_shutdown.store(true, Ordering::SeqCst);
            if let Some(handle) = handler_status.get() {
                let _ = handle.set_service_status(ServiceStatus {
                    service_type: ServiceType::OWN_PROCESS,
                    current_state: ServiceState::StopPending,
                    controls_accepted: ServiceControlAccept::empty(),
                    exit_code: ServiceExitCode::Win32(0),
                    checkpoint: 0,
                    wait_hint: service::STOP_WAIT_HINT,
                    process_id: None,
                });
            }
            ServiceControlHandlerResult::NoError
        }
        _ => ServiceControlHandlerResult::NotImplemented,
    })
    .context("could not register the service control handler")?;
    let _ = status_slot.set(status_handle);

    let running_status = ServiceStatus {
        service_type: ServiceType::OWN_PROCESS,
        current_state: ServiceState::Running,
        controls_accepted: ServiceControlAccept::STOP | ServiceControlAccept::SHUTDOWN,
        exit_code: ServiceExitCode::Win32(0),
        checkpoint: 0,
        wait_hint: Duration::default(),
        process_id: None,
    };
    status_handle.set_service_status(running_status.clone())?;

    service::run_loop(shutdown);

    status_handle.set_service_status(ServiceStatus {
        current_state: ServiceState::Stopped,
        controls_accepted: ServiceControlAccept::empty(),
        ..running_status
    })?;

    Ok(())
}

// ---------------------------------------------------------------------------------------------
// The remote control session helper (`--remote-session-helper`).
// ---------------------------------------------------------------------------------------------

/// One remote control session, then exit.
///
/// Launched by the service as SYSTEM inside the logged-in session — see `session_launcher` for why
/// it cannot be the user's own token and `remote_desktop` for what the privilege buys. Not a mode
/// anyone should run by hand: without the service on the other end of the pipe it connects to
/// nothing and exits.
///
/// Logs to the service's own log rather than a per-user one. It runs as SYSTEM, so it has no user
/// state directory to write to, and a session's story is more useful interleaved with the service's
/// than in a file of its own.
fn run_remote_session_helper() -> Result<()> {
    logging::init(&config::service_log_path());
    logging::info("kintsugi-agent (--remote-session-helper) starting");

    let result = remote_session::run();

    match &result {
        Ok(()) => logging::info("the remote control session helper is exiting"),
        Err(err) => logging::error(&format!("the remote control session helper failed: {err:#}")),
    }

    result
}

// ---------------------------------------------------------------------------------------------
// The per-user half (`--agent`) — the counterpart to the macOS agent's LaunchAgent.
// ---------------------------------------------------------------------------------------------

/// Runs continuously in the logged-in user's own session — not elevated, so it can show dialogs and
/// notifications directly, no privilege trickery needed — tracking the fleet-wide patching policy
/// and driving the confirm/delay/patch flow once it's due, plus the notification-area icon
/// (progress / next check-in / next due / Check In Now / Patch Now).
///
/// Splits into two threads for the same reason the macOS agent does: the UI has to keep pumping
/// messages to stay responsive, while the scheduler blocks on queue round trips, five-minute
/// warnings, and modal dialogs. So the *scheduler* runs on a background thread and the *UI* keeps
/// the main one. They talk to each other one direction each: the scheduler pushes `AgentStatus` and
/// `CheckInStatus` updates to the menu (`report`, ultimately a posted window message), and a click on
/// "Check In Now" or "Patch Now" sends a `MenuAction` back to the scheduler over `menu_rx` — see
/// `tray_menu` and `status`.
///
/// Unlike its macOS counterpart, this process holds no mutual-TLS identity and makes no network call
/// at all: it asks the service for the work list and for each patch, over the queue. See `queue` for
/// why.
fn run_ui_agent() -> Result<()> {
    let state_dir = config::user_state_dir()?;
    logging::init(&state_dir.join("agent.log"));
    logging::info(&format!("kintsugi-agent (--agent) starting; state_dir={}", state_dir.display()));

    let policy_cache_path = config::policy_cache_path();
    let schedule_state_path = state_dir.join("schedule.json");

    let (menu_tx, menu_rx) = mpsc::channel();
    let report: StatusReporterFn = tray_menu::report_status;

    // The wait for a first policy happens on the scheduler's thread, not here, so the icon appears
    // immediately — see `wait_for_policy` for what used to happen instead.
    std::thread::spawn(move || {
        let current_policy = wait_for_policy(&policy_cache_path, &menu_rx, report);
        let state = ScheduleState::load_or_default(&schedule_state_path, &current_policy);
        run_scheduler(current_policy, state, schedule_state_path, policy_cache_path, menu_rx, report);
    });

    // Blocks for the rest of the process's life — this call never returns normally.
    tray_menu::run(menu_tx)
}

/// Blocks until the service has published a patching policy, which is the first thing the scheduler
/// needs and the one thing this process cannot fetch for itself: it holds no mutual-TLS identity,
/// and `/api/patching-policy` is inside nginx's client-certificate regex, so the service writes the
/// cache and this side reads it (see `service::check_in` and `queue`).
///
/// Ordinarily this returns on the first look — the service checks in at startup, well before anyone
/// logs in. It matters when that check-in fails. A policy refresh is the *last* step of a check-in,
/// after the heartbeat, the host registration and the inventory have all succeeded, so a single bad
/// network moment leaves no cache and the next attempt is an hour away.
///
/// This used to run in `run_ui_agent` itself, ahead of `tray_menu::run`, which meant that hour was
/// spent with **no notification-area icon at all** — not an error, not a "waiting" state, nothing —
/// and the only way to find out why was to read `agent.log`. That shipped, and cost an afternoon on
/// a host whose network dropped about 40% of connections: the agent was behaving correctly at every
/// layer and looked, to the person in front of it, uninstalled. So the icon now goes up first and
/// this reports `WaitingForPolicy` behind it.
///
/// Servicing `menu_rx` here rather than sleeping is the other half of that. "Check In Now" is what
/// asks the service to try again, so it is the one action that can end this state from the outside —
/// leaving it unanswered would have been a live-looking button that did nothing until the wait was
/// already over. "Patch Now" is greyed by `AgentStatus::WaitingForPolicy` and ignored if a click was
/// already in flight when the greying took effect.
/// A click in the "Install software" submenu: asks the service to install the offered title, and
/// tells the person who clicked how it went.
///
/// The request is the ordinary `AppPatch`, carrying only the name — the service re-fetches the row
/// and verifies the script, the action and every secret before running anything (see
/// `service::Agent::patch_application`), so this process never handles a script or a secret, and
/// nothing secret ever lands in the queue directory, which `Users` can write. On a thread of its
/// own, because the answer can take up to `upgrade::ACTION_WAIT` and the scheduler must stay free
/// for the menu meanwhile. Kept in step with the other two agents' `install_self_service`.
fn install_self_service(index: usize) {
    let Some(name) = self_service::shown_name(index) else {
        logging::warn(&format!("Install software item {index} no longer names anything; ignoring the click"));
        return;
    };
    logging::info(&format!("asking the agent service to install the self-service title {name}"));
    dialogs::notify(&format!("Installing {name}"), "You'll be told when it's done.");

    std::thread::spawn(move || match queue::submit(&config::queue_dir(), queue::RequestKind::AppPatch, &name, upgrade::ACTION_WAIT) {
        Ok(result) if result.success => {
            logging::info(&format!("self-service install of {name} finished"));
            dialogs::notify(&format!("{name} is installed"), "It's ready to use.");
        }
        Ok(result) => {
            logging::error(&format!("self-service install of {name} failed: {}", result.output.trim()));
            dialogs::notify(&format!("{name} didn't install"), "If this keeps happening, contact IT.");
        }
        Err(err) => {
            logging::error(&format!("could not ask the agent service to install {name}: {err:#}"));
            dialogs::notify(&format!("{name} didn't install"), "Try again in a few minutes.");
        }
    });
}

fn wait_for_policy(
    policy_cache_path: &std::path::Path,
    menu_rx: &mpsc::Receiver<MenuAction>,
    report: StatusReporterFn,
) -> policy::PatchingPolicy {
    if let Some(policy) = policy::load_cached(policy_cache_path) {
        return policy;
    }

    logging::info("waiting for the agent service to publish the patching policy");
    report(AgentStatus::WaitingForPolicy);
    show_scheduled_check_in();

    loop {
        match menu_rx.recv_timeout(AGENT_POLL_INTERVAL) {
            Ok(MenuAction::CheckInNow) => {
                logging::info("Check In Now clicked while waiting for a policy");
                tray_menu::report_check_in(CheckInStatus::InProgress);
                checkin_schedule::request_now(&config::queue_dir());
                show_scheduled_check_in();
            }
            // Nothing about installing an offered title needs a patching policy — the service
            // fetches and verifies the row itself — so this is served here as everywhere else.
            Ok(MenuAction::Install(index)) => install_self_service(index),
            // Greyed while this state is showing, so this only happens for a click already on its
            // way. Saying so beats silence for an action the user just took — the same reasoning
            // `run_scheduler` applies to a Patch Now that arrives mid-cycle.
            Ok(MenuAction::PatchNow) => {
                logging::info("Patch Now ignored: no patching policy has reached this host yet");
                dialogs::notify("Not ready yet", "This computer is still setting up. Try again in a few minutes.");
            }
            Err(mpsc::RecvTimeoutError::Timeout) => {}
            Err(mpsc::RecvTimeoutError::Disconnected) => {
                // As in `run_scheduler`: the sender lives in tray_menu for the life of the process,
                // so this should never happen, but polling beats spinning on an instant error.
                logging::error("menu action channel disconnected unexpectedly");
                std::thread::sleep(AGENT_POLL_INTERVAL);
            }
        }

        if let Some(policy) = policy::load_cached(policy_cache_path) {
            logging::info("the agent service published a patching policy; scheduling resumes");
            return policy;
        }
        logging::info("waiting for the agent service to publish the patching policy");
    }
}

/// Puts the service's hourly minute back on the menu's "Next check-in" line, which also un-greys
/// both actions after a `CheckInStatus::InProgress`. `run_scheduler` gets this for free by clearing
/// `shown_check_in` and letting its next tick recompute; the wait above has no such tick.
fn show_scheduled_check_in() {
    tray_menu::report_check_in(CheckInStatus::Scheduled {
        next_epoch: checkin_schedule::next_check_in_epoch(&config::checkin_schedule_path()),
    });
}

/// Writes this process's output to the console of whatever started it, if that has one — so
/// `--check-in` run by an administrator still prints what it did, though the binary is now a
/// windowed program (see the `windows_subsystem` attribute at the top of this file).
///
/// It used to be the other way round: a console program that hid its own window, on the reasoning
/// that this kept `--check-in` readable. Windows 11 broke the hiding (the window is a Windows
/// Terminal tab that `GetConsoleWindow` does not return), so the tray went in front of users.
///
/// The cost is that a shell does not wait for a windowed program: `cmd` and PowerShell print their
/// prompt at once and the output lands after it. `start /wait kintsugi-agent.exe --check-in`, or
/// `Start-Process -Wait -NoNewWindow` as `install.ps1` does, waits and keeps the exit code.
///
/// A handle the parent already redirected (a pipe, a file) is kept; only a missing one is pointed
/// at the console. Nothing here fails loudly: with no console to borrow — the service under the
/// SCM, the restart helper under the service — `logging`'s file is still written.
fn attach_parent_console() {
    use windows_sys::Win32::Foundation::{GENERIC_READ, GENERIC_WRITE, INVALID_HANDLE_VALUE};
    use windows_sys::Win32::Storage::FileSystem::{CreateFileW, FILE_SHARE_READ, FILE_SHARE_WRITE, OPEN_EXISTING};
    use windows_sys::Win32::System::Console::{
        AttachConsole, GetStdHandle, SetStdHandle, ATTACH_PARENT_PROCESS, STD_ERROR_HANDLE, STD_OUTPUT_HANDLE,
    };

    // SAFETY: AttachConsole and GetStdHandle take plain values. CreateFileW gets a live,
    // NUL-terminated name and null optional pointers; its result is checked before SetStdHandle
    // stores it. The handle is deliberately never closed: it is this process's stdout until exit.
    unsafe {
        if AttachConsole(ATTACH_PARENT_PROCESS) == 0 {
            return;
        }
        for which in [STD_OUTPUT_HANDLE, STD_ERROR_HANDLE] {
            let current = GetStdHandle(which);
            if !current.is_null() && current != INVALID_HANDLE_VALUE {
                continue;
            }
            let console_output = win32::wide("CONOUT$");
            let handle = CreateFileW(
                console_output.as_ptr(),
                GENERIC_READ | GENERIC_WRITE,
                FILE_SHARE_READ | FILE_SHARE_WRITE,
                std::ptr::null(),
                OPEN_EXISTING,
                0,
                std::ptr::null_mut(),
            );
            if handle != INVALID_HANDLE_VALUE {
                SetStdHandle(which, handle);
            }
        }
    }
}


/// How a patch cycle is started — `patch_cycle::run` for a naturally due one, `run_now` for a
/// "Patch Now" click. Their signatures are identical, so `spawn_cycle` is handed whichever one is
/// meant rather than a flag to branch on.
type CycleFn = fn(&policy::PatchingPolicy, &mut ScheduleState, &StatusReporter);

/// Runs one patch cycle on its own thread, handing it the schedule state for the duration and
/// getting it back when it finishes.
///
/// A thread rather than a plain call, because the confirmation dialog stands there for a whole
/// delay period and the scheduler used to be parked inside it for all of it: the "Next check-in"
/// line stopped updating, and a menu click sat in the channel until the dialog came down, which
/// from the menu looks like the item did nothing. (The Linux agent had a third and worse symptom —
/// see its own copy of this comment.)
///
/// The state goes *with* the cycle rather than being shared behind a lock: a mutex held for the
/// hours a dialog can stand there would have moved the block rather than removed it, since
/// `is_due` would then be the thing waiting. So `state` being `None` in `run_scheduler`'s loop is
/// exactly "a cycle is in flight", and it is what stops a second one starting.
///
/// Everything else is cheap to hand over — the policy is a small struct, `report` is a function
/// pointer — and each cycle works from the policy as it was when it started, which is what the
/// previous arrangement did too: the loop's re-read only ever affected the *next* cycle.
fn spawn_cycle(
    cycle: CycleFn,
    policy: &policy::PatchingPolicy,
    mut state: ScheduleState,
    report: StatusReporterFn,
) -> std::thread::JoinHandle<ScheduleState> {
    let policy = policy.clone();
    std::thread::spawn(move || {
        cycle(&policy, &mut state, &report);
        state
    })
}

/// `spawn_cycle` for a forced run — see `patch_cycle::run_forced`.
///
/// A second function rather than another `CycleFn`, because a forced run carries something the
/// other two do not: the list of applications an administrator named. `CycleFn` is a plain function
/// pointer precisely so the scheduler hands over *which* cycle without a flag to branch on, and
/// widening it to a closure to carry one argument would cost that everywhere. Everything else here
/// — the thread, the state by ownership, the join site — is `spawn_cycle`'s, for its reasons.
///
/// Kept identical in the other two agents.
fn spawn_forced_cycle(
    policy: &policy::PatchingPolicy,
    mut state: ScheduleState,
    report: StatusReporterFn,
    application_names: Vec<String>,
) -> std::thread::JoinHandle<ScheduleState> {
    let policy = policy.clone();
    std::thread::spawn(move || {
        patch_cycle::run_forced(&policy, &mut state, &report, &application_names);
        state
    })
}

/// Whether a due cycle may start now, which turns on whether its confirmation dialog would be
/// seen — see `presence`. Logs the hold when it begins and again when it lifts, and nothing in
/// between: `held_for` is the reason last logged, kept by the caller across ticks.
///
/// Only the automatic entry point asks. "Patch Now" is a click, so somebody is looking; a forced
/// run is an administrator's decision that the interruption is warranted, and it shows nothing
/// that needs an answer (see `patch_cycle::run_forced`). Kept identical in the other two agents.
fn prompt_can_be_seen(held_for: &mut Option<String>) -> bool {
    match presence::obstruction() {
        Some(reason) => {
            if held_for.as_deref() != Some(reason.as_str()) {
                logging::info(&format!("patch cycle is due, but held until its prompt can be seen: {reason}"));
                *held_for = Some(reason);
            }
            false
        }
        None => {
            if held_for.take().is_some() {
                logging::info("the screen can be seen again; starting the held patch cycle");
            }
            true
        }
    }
}

/// The background half of `run_ui_agent` — see its doc comment for why this is a separate thread.
/// Reports its state to the menu via `report` at every meaningful transition, and treats a "Patch
/// Now" click the same as a naturally due cycle except it skips the confirm/delay step entirely (see
/// `patch_cycle::run_now`). A "Check In Now" click goes to the service through the queue (see
/// `checkin_schedule::request_now`).
fn run_scheduler(
    mut current_policy: policy::PatchingPolicy,
    state: ScheduleState,
    schedule_state_path: std::path::PathBuf,
    policy_cache_path: std::path::PathBuf,
    menu_rx: mpsc::Receiver<MenuAction>,
    report: StatusReporterFn,
) {
    report(AgentStatus::Idle { next_due_epoch: state.next_due_epoch() });

    // `None` for exactly as long as a cycle owns the schedule state — see `spawn_cycle`, which is
    // also why this loop can be sure it is never running two.
    let mut state = Some(state);
    let mut in_flight: Option<std::thread::JoinHandle<ScheduleState>> = None;

    // The service's schedule, as last shown in the menu. Re-read every tick — the service persists
    // a minute on its first run and the server may move it on any check-in — but only pushed to the
    // menu when the answer changes, which is once an hour: `tray_menu::format_due` shells out to
    // PowerShell, and there is no reason to do that once a minute for a line that has not moved.
    let checkin_schedule_path = config::checkin_schedule_path();
    let mut shown_check_in: Option<CheckInStatus> = None;

    // Why a due cycle is being held rather than started — see `prompt_can_be_seen`. Kept across
    // ticks only so the hold is logged when it begins and when it ends, not once a minute.
    let mut held_for: Option<String> = None;

    // Forced patch runs are collected on a thread of their own here, unlike the other two agents —
    // see `forced_patch_run::spawn_poller` for why the queue makes that necessary.
    let forced_rx = forced_patch_run::spawn_poller(AGENT_POLL_INTERVAL);

    // What the poller has collected and no cycle has run yet. A buffer rather than a straight read,
    // because the poller keeps collecting while a cycle is in flight — the schedule state is inside
    // that cycle (see `spawn_cycle`), so there is nothing to start a second one with — and an
    // instruction the server has already marked collected has nowhere else to come back from.
    let mut pending_forced: Vec<String> = Vec::new();

    loop {
        // Takes the schedule state back from a cycle that has finished, and says so. Reporting
        // here rather than trusting the cycle to is what covers its early returns — an
        // unreachable service, nothing to patch, a dialog that would not launch — since those
        // report nothing and would otherwise leave the menu greyed on "waiting for your answer"
        // for a prompt that is no longer there.
        if in_flight.as_ref().is_some_and(|handle| handle.is_finished()) {
            match in_flight.take().expect("just checked that a handle is there").join() {
                Ok(finished) => state = Some(finished),
                Err(_) => {
                    // Nothing in a cycle is expected to panic, but a scheduler that quietly
                    // stopped scheduling would be the worst possible way to find out: reload from
                    // disk (the state is saved on every change, so disk is the freshest copy) and
                    // carry on, so a repeating panic shows up as a repeating log line rather than
                    // as a host that silently never patches again.
                    logging::error("the patch cycle thread panicked; reloading the schedule from disk and carrying on");
                    state = Some(ScheduleState::load_or_default(&schedule_state_path, &current_policy));
                }
            }
            // Reported only when the cycle is over *and* nothing is pending. An unanswered
            // dialog deliberately leaves the state due right now (see
            // `ScheduleState::register_unanswered_prompt`), so the next tick re-asks within the
            // minute — and announcing "next patch due: <a moment ago>" with both actions enabled
            // in between would flicker the menu once per delay period for the whole budget.
            if let Some(current) = state.as_ref().filter(|current| !current.is_due()) {
                report(AgentStatus::Idle { next_due_epoch: current.next_due_epoch() });
            }
        }

        // Re-read rather than re-fetch: the service refreshes this file on its own schedule (see
        // `service::check_in`), so picking up a policy change here is a local file read, not a
        // network call this process couldn't make anyway.
        if let Some(refreshed) = policy::load_cached(&policy_cache_path) {
            current_policy = refreshed;
        }

        let next_check_in = CheckInStatus::Scheduled { next_epoch: checkin_schedule::next_check_in_epoch(&checkin_schedule_path) };
        if shown_check_in != Some(next_check_in) {
            tray_menu::report_check_in(next_check_in);
            shown_check_in = Some(next_check_in);
        }

        // Waits on the channel rather than sleeping and polling it once per iteration — a click in
        // the menu wakes this immediately instead of sitting unnoticed for up to AGENT_POLL_INTERVAL,
        // which from the menu just looks like the item did nothing.
        match menu_rx.recv_timeout(AGENT_POLL_INTERVAL) {
            Ok(MenuAction::PatchNow) => {
                logging::info("scheduler received the Patch Now signal");
                match state.take() {
                    Some(owned) => in_flight = Some(spawn_cycle(patch_cycle::run_now, &current_policy, owned, report)),
                    // The menu greys both actions whenever this can happen, so getting here means a
                    // click was already on its way — which is worth saying, since this is an
                    // explicit action the user just took and silence would read as it being lost.
                    None => {
                        logging::info("Patch Now ignored: a patch cycle is already running");
                        dialogs::notify("Already updating", "Updates are already being installed.");
                    }
                }
            }
            Ok(MenuAction::Install(index)) => install_self_service(index),
            Ok(MenuAction::CheckInNow) => {
                logging::info("scheduler received the Check In Now signal");
                tray_menu::report_check_in(CheckInStatus::InProgress);
                checkin_schedule::request_now(&config::queue_dir());
                // Forces the schedule line back at the top of the next tick, whatever it now says —
                // the server may just have moved this host's minute.
                shown_check_in = None;
            }
            Err(mpsc::RecvTimeoutError::Timeout) => {
                for collected in forced_rx.try_iter() {
                    logging::info(&format!(
                        "collected {} forced patch run(s): {}",
                        collected.len(),
                        collected.iter().map(|run| format!("{} ({})", run.application_name, run.id)).collect::<Vec<_>>().join(", ")
                    ));
                    pending_forced.extend(collected.into_iter().map(|run| run.application_name));
                }

                // An administrator's forced run takes precedence over a naturally due cycle, because
                // this is the emergency path and the two cannot run at once — the schedule state
                // goes *into* a cycle by ownership (see `spawn_cycle`). Nothing is lost by the
                // ordering: a forced run does not register a completed cycle (see
                // `patch_cycle::run_forced`), so a cycle that was due stays due and the next tick
                // starts it.
                if !pending_forced.is_empty() && state.is_some() {
                    let owned = state.take().expect("just checked that the state is here");
                    in_flight = Some(spawn_forced_cycle(&current_policy, owned, report, std::mem::take(&mut pending_forced)));
                } else if state.as_ref().is_some_and(|current| current.is_due()) && prompt_can_be_seen(&mut held_for) {
                    let owned = state.take().expect("just checked that the state is here");
                    in_flight = Some(spawn_cycle(patch_cycle::run, &current_policy, owned, report));
                } else if let Some(names) = state.as_ref().filter(|current| !current.is_due()).map(|current| current.due_retries()).filter(|names| !names.is_empty() && prompt_can_be_seen(&mut held_for)) {
                    // A failed patch whose backoff is over, while the scheduled cycle is not due. It
                    // goes through `run_forced`: no confirm/delay dialog (the person already agreed to
                    // this patch, so a retry must neither ask again nor spend the delay budget) and no
                    // `register_completed` (the real cycle keeps its own time). See `retry_state`.
                    // Held until the screen can be seen, as a due cycle is: this is the agent's own
                    // decision, not an administrator's, and run_forced's "save your work" notice is
                    // the only warning before the app is closed.
                    logging::info(&format!("retrying failed patch(es): {}", names.join(", ")));
                    let owned = state.take().expect("just checked that the state is here");
                    in_flight = Some(spawn_forced_cycle(&current_policy, owned, report, names));
                }
            }
            Err(mpsc::RecvTimeoutError::Disconnected) => {
                // The sender lives in tray_menu for the whole life of the process, so this should
                // never happen — but if it does, fall back to plain polling rather than spin-looping
                // on an instantly-erroring recv.
                logging::error("menu action channel disconnected unexpectedly");
                std::thread::sleep(AGENT_POLL_INTERVAL);
            }
        }
    }
}
