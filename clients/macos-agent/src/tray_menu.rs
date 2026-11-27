use std::cell::RefCell;
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::mpsc::Sender;
use std::sync::Arc;

use anyhow::{anyhow, Context, Result};
use objc2::MainThreadMarker;
use objc2_app_kit::{NSApplication, NSApplicationActivationPolicy, NSScreen};
use objc2_foundation::NSRect;
use tray_icon::{Icon, MouseButtonState, TrayIcon, TrayIconBuilder, TrayIconEvent};

use crate::logging;
use crate::self_service::{self, Offer};
use crate::status::{AgentStatus, CheckInStatus, MenuAction};
use crate::tray_popup::{self, InstallState, Panel, Press};

const MENU_BAR_ICON_BYTES: &[u8] = include_bytes!("../assets/menu-bar-icon.png");
const TOOLTIP: &str = "Kintsugi Patching";

/// Everything the menu bar popup shows, kept between clicks: the popup is built from this each time
/// it opens (`panel`) and redrawn from it while it is open. The menu these lines used to be the items
/// of went in 0.27.0 (IP-976); the popup is `tray_popup`.
struct MenuState {
    // Kept alive for as long as the app runs — dropping it removes the icon from the menu bar.
    tray_icon: TrayIcon,
    menu_tx: Sender<MenuAction>,
    end_remote_session: Arc<AtomicBool>,

    check_in_line: String,
    status_line: String,
    progress_line: String,

    // Whether each half of the process is busy. The two actions are enabled only while neither is:
    // the scheduler thread serves both actions, so a click during either would sit in the channel
    // and run the moment the current one finished — which from the popup looks like a button that
    // did nothing and then, minutes later, did something unasked.
    patching: bool,
    checking_in: bool,
    // Set while `AgentStatus::WaitingForPolicy` is showing, and the one place the two actions
    // diverge: there is no schedule to patch against, but a check-in is what enrolls a host that
    // has not yet managed to, so that button has to stay live or the state has no exit but waiting.
    awaiting_policy: bool,

    // Who is controlling this Mac, while a screen session runs — see `report_remote_session`.
    remote_session: Option<String>,
    // The titles an administrator has offered this host, as the scheduler last read them from the
    // root daemon's cache, and how each install this process asked for is going.
    offers: Vec<Offer>,
    installs: Vec<(String, InstallState)>,
}

impl MenuState {
    /// What the popup shows now. Records the offers it lists (`self_service::remember_shown`), so a
    /// click's index is resolved against what is on screen rather than against a cache the next
    /// check-in may have rewritten — the snapshot the Windows popup and the Linux menu keep too.
    fn panel(&self) -> Panel {
        let enabled = !self.patching && !self.checking_in;
        let mut offers = self.offers.clone();
        offers.truncate(tray_popup::MAX_OFFERS);
        self_service::remember_shown(&offers);
        let state_of = |name: &str| self.installs.iter().find(|(existing, _)| existing == name).map(|(_, state)| *state);
        Panel {
            lines: [&self.status_line, &self.progress_line, &self.check_in_line].into_iter().filter(|line| !line.is_empty()).cloned().collect(),
            remote_session: self.remote_session.clone(),
            check_in_enabled: enabled,
            patch_enabled: enabled && !self.awaiting_policy,
            offers: offers.into_iter().map(|offer| {
                let state = state_of(&offer.name);
                (offer, state)
            }).collect(),
        }
    }

    fn set_install(&mut self, name: &str, state: InstallState) {
        self.installs.retain(|(existing, _)| existing != name);
        self.installs.push((name.to_string(), state));
    }
}

thread_local! {
    // Only ever touched from the main thread: populated once by `run` before the app loop starts,
    // and read inside blocks that are already running on the main thread by the time they execute
    // (dispatched there via `update`, even though the reporters are normally *called* from the
    // background scheduler thread). `TrayIcon` is `Rc`-based and so isn't `Send`; a thread-local
    // sidesteps that rather than fighting it, since nothing here actually needs to cross threads.
    static MENU_STATE: RefCell<Option<MenuState>> = const { RefCell::new(None) };
}

/// Sets up the menu bar icon and runs AppKit's main event loop for the rest of the process's
/// life — must be called on the main thread (a hard Cocoa requirement for any UI, including a
/// status item) and never returns normally. `menu_tx` is how a click on "Check in now", "Install
/// updates now" or an Install button reaches the scheduler thread.
pub fn run(menu_tx: Sender<MenuAction>, end_remote_session: Arc<AtomicBool>) -> Result<()> {
    let mtm = MainThreadMarker::new().context("the menu bar can only be set up on the main thread")?;

    let app = NSApplication::sharedApplication(mtm);
    // A pure menu-bar utility: no Dock icon, no Cmd-Tab entry, never steals focus on launch.
    app.setActivationPolicy(NSApplicationActivationPolicy::Accessory);

    let tray_icon = build_tray_icon()?;
    MENU_STATE.with(|cell| {
        *cell.borrow_mut() = Some(MenuState {
            tray_icon,
            menu_tx,
            end_remote_session,
            check_in_line: "Next check-in: not yet scheduled".to_string(),
            status_line: "Loading patching status\u{2026}".to_string(),
            progress_line: String::new(),
            patching: false,
            checking_in: false,
            awaiting_policy: false,
            remote_session: None,
            offers: Vec::new(),
            installs: Vec::new(),
        });
    });
    logging::info("menu bar icon created");

    // Either button opens the popup, on the press, as a menu would; a second press closes it. The
    // handler runs on the main thread, inside the status item's own mouse handling.
    TrayIconEvent::set_event_handler(Some(|event: TrayIconEvent| {
        if let TrayIconEvent::Click { button_state: MouseButtonState::Down, .. } = event {
            toggle_popup();
        }
    }));

    // Blocks for the rest of the process's life, running the standard Cocoa event loop — this is
    // what actually makes the status item clickable/interactive, not just visible.
    app.run();

    Ok(())
}

/// Opens or closes the popup, under the status item on whichever display it was clicked on.
fn toggle_popup() {
    let Some(mtm) = MainThreadMarker::new() else { return };
    let opening = MENU_STATE.with(|cell| {
        let borrowed = cell.borrow();
        let state = borrowed.as_ref()?;
        let icon_window = state.tray_icon.ns_status_item().and_then(|item| item.button(mtm)).and_then(|button| button.window());
        let icon = icon_window.as_ref().map(|window| window.frame());
        let screen = icon_window.and_then(|window| window.screen()).or_else(|| NSScreen::mainScreen(mtm));
        Some((state.panel(), icon, screen.map_or(NSRect::ZERO, |screen| screen.visibleFrame())))
    });
    if let Some((panel, icon, visible)) = opening {
        tray_popup::toggle(panel, icon, visible);
    }
}

/// Runs `change` against the menu state on the main thread, then redraws the popup if it is open.
/// How every reporter below reaches AppKit, since they are called from the scheduler thread.
fn update(change: impl FnOnce(&mut MenuState) + Send + 'static) {
    dispatch::Queue::main().exec_async(move || {
        let panel = MENU_STATE.with(|cell| {
            let mut borrowed = cell.borrow_mut();
            let state = borrowed.as_mut()?;
            change(state);
            // Built only while it is on screen: `panel` records what it lists as what was shown.
            tray_popup::is_open().then(|| state.panel())
        });
        // Outside the borrow: redrawing can re-enter AppKit, and AppKit this module.
        if let Some(panel) = panel {
            tray_popup::refresh(panel);
        }
    });
}

/// Hands a press in the popup on. Main thread only, once the press is over (`tray_popup::pressed`).
/// An install is marked as started here, on the click, rather than when the scheduler gets to it:
/// the scheduler may be busy with a check-in or a patch cycle, and a button that did nothing visible
/// until then would be clicked again.
pub(crate) fn act(press: Press) {
    logging::info(&format!("{press:?} clicked in the menu bar popup"));
    let action = match press {
        Press::ChatWithSupport => {
            crate::chat_window::open_from_menu();
            return;
        }
        // A flag rather than a channel, because the session loop is polling anyway (it has to, to
        // interleave reading input with sending frames) and because this must work whether or not a
        // session is currently running — a click that arrives as one is already ending has nowhere
        // to be delivered.
        Press::EndRemoteSession => {
            MENU_STATE.with(|cell| {
                if let Some(state) = cell.borrow().as_ref() {
                    state.end_remote_session.store(true, Ordering::SeqCst);
                }
            });
            return;
        }
        Press::Menu(action) => action,
    };

    let sent = MENU_STATE.with(|cell| {
        let mut borrowed = cell.borrow_mut();
        let state = borrowed.as_mut()?;
        if let MenuAction::Install(index) = action {
            let name = self_service::shown_name(index)?;
            state.set_install(&name, InstallState::Installing);
        }
        Some(state.menu_tx.send(action))
    });
    match sent {
        Some(Ok(())) => update(|_| {}),
        Some(Err(err)) => logging::error(&format!("could not signal the scheduler thread: {err}")),
        // `--preview-popups`, which has no scheduler, or an index that names nothing any more.
        None => logging::info(&format!("{action:?} not sent: nothing here to act on it")),
    }
}

/// Pushes a status update to the menu bar popup. Safe to call from any thread — the scheduler
/// thread is the only real caller — since the actual UI update is marshaled onto the main thread via
/// GCD (`dispatch_async` to the main queue), because AppKit objects may only be touched from there.
pub fn report_status(status: AgentStatus) {
    // A window, unlike the popup, is visible without the user having to think to go looking for it
    // — opened the moment there's something to show, closed again once idle. On the main thread
    // with everything else, in the order the statuses arrived.
    let window_status = status.clone();
    dispatch::Queue::main().exec_async(move || match &window_status {
        // `PreFetching` keeps the window closed on purpose. The window exists to hold somebody's
        // attention through a patch run they agreed to; a background download they never asked
        // about has no claim on the screen, and one that floated up for an hour would be a worse
        // nuisance than the greyed button this replaced. The popup's line is where it belongs, for
        // whoever goes looking.
        AgentStatus::Idle { .. }
        | AgentStatus::WaitingForPolicy
        | AgentStatus::AwaitingAnswer
        | AgentStatus::PatchingInBackground { .. }
        | AgentStatus::PreFetching { .. } => crate::progress_window::hide(),
        AgentStatus::Patching { current, completed, total } => crate::progress_window::show_and_update(current, *completed, *total),
    });

    update(move |state| {
        match &status {
            AgentStatus::Idle { next_due_epoch } => {
                state.status_line = format!("Next patch due: {}", format_due(*next_due_epoch));
                state.progress_line = "Status: idle".to_string();
                state.patching = false;
            }
            AgentStatus::WaitingForPolicy => {
                state.status_line = "Next patch due: not scheduled yet".to_string();
                state.progress_line = "Status: waiting for the patching policy".to_string();
                state.patching = false;
            }
            // Greyed like `Patching` via the same flag, but with the progress window left closed
            // above: a prompt awaiting an answer is not progress, and a window claiming otherwise
            // would be sitting on top of the dialog it is describing.
            AgentStatus::AwaitingAnswer => {
                state.status_line = "Waiting for your answer".to_string();
                state.progress_line = "Status: a prompt is on screen".to_string();
                state.patching = true;
            }
            AgentStatus::Patching { current, completed, total } => {
                state.status_line = format!("Patching: {current}");
                state.progress_line = if *total > 0 {
                    format!("Progress: {}", crate::dialogs::progress_bar(*completed, *total))
                } else {
                    "Progress: starting\u{2026}".to_string()
                };
                state.patching = true;
            }
            AgentStatus::PatchingInBackground { current } => {
                state.status_line = format!("Updating in the background: {current}");
                state.progress_line = "Status: patching an application that is not open".to_string();
                state.patching = true;
            }
            // Note what is missing: `state.patching = true`. The daemon fetching an update in the
            // background is not a reason to stop somebody checking in — see
            // `AgentStatus::PreFetching`.
            AgentStatus::PreFetching { current, percent } => {
                state.status_line = format!("Downloading: {current}");
                state.progress_line = format!("Progress: {}", crate::dialogs::progress_bar(usize::from(*percent), 100));
            }
        }
        state.awaiting_policy = matches!(status, AgentStatus::WaitingForPolicy);
    });
}

/// Pushes the daemon's check-in schedule to the popup's "Next check-in" line, and greys both
/// actions while a "Check in now" is in flight. Safe to call from any thread, like `report_status`,
/// and for the same reason.
///
/// Separate from `report_status` because the two describe different processes: the patch cycle is
/// this one's, the check-in is the root daemon's, and either can be busy while the other is idle.
pub fn report_check_in(status: CheckInStatus) {
    update(move |state| match status {
        CheckInStatus::Scheduled { next_epoch: Some(epoch) } => {
            state.check_in_line = format!("Next check-in: {}", format_due(epoch));
            state.checking_in = false;
        }
        CheckInStatus::Scheduled { next_epoch: None } => {
            state.check_in_line = "Next check-in: not yet scheduled".to_string();
            state.checking_in = false;
        }
        CheckInStatus::InProgress => {
            state.check_in_line = "Checking in with the server\u{2026}".to_string();
            state.checking_in = true;
        }
    });
}

/// Replaces the popup's "Install software" list, which is absent while nothing is offered. Safe to
/// call from any thread, like `report_status`; the scheduler calls it only when the cached list has
/// changed.
pub fn report_self_service(offers: Vec<Offer>) {
    update(move |state| state.offers = offers);
}

/// Records how a self-service install of `name` is going, for the popup's row. Safe to call from
/// any thread, like `report_status`. Set to `Installing` by the click itself (`act`).
pub fn report_install(name: &str, install: InstallState) {
    let name = name.to_string();
    update(move |state| state.set_install(&name, install));
}

/// Shows or hides the remote control block at the top of the popup.
///
/// **This is the only thing on screen that says who is controlling this Mac, and that makes it part
/// of the security model rather than a nicety.** macOS shows its own screen-recording indicator in
/// the menu bar, but that says something is being captured, not who by and not how to stop it.
/// Somebody who allowed a session — or who walked up to a Mac where one is running — needs to be
/// able to see whose it is and end it without finding an administrator. One click on the icon does
/// both, as the menu did.
///
/// Safe to call from any thread, like `report_status`, and for the same reason.
pub fn report_remote_session(requested_by: Option<String>) {
    update(move |state| state.remote_session = requested_by);
}

fn build_tray_icon() -> Result<TrayIcon> {
    let icon = load_icon()?;
    // No menu: a click opens the popup instead (see `run`). With none attached, `tray-icon` only
    // highlights the icon while the mouse is down and reports the click.
    TrayIconBuilder::new()
        .with_icon(icon)
        .with_icon_as_template(true)
        .with_tooltip(TOOLTIP)
        .build()
        .map_err(|e| anyhow!("{e}"))
}


fn load_icon() -> Result<Icon> {
    let decoded = image::load_from_memory(MENU_BAR_ICON_BYTES)
        .context("failed to decode the embedded menu bar icon")?
        .into_rgba8();
    let (width, height) = decoded.dimensions();
    Icon::from_rgba(decoded.into_raw(), width, height).map_err(|e| anyhow!("{e}"))
}

/// Renders `epoch` in the Mac's own local time (not UTC) — what the person looking at the menu
/// actually keeps their clock in. Shells out to `date` rather than using the `time` crate's own
/// local-offset support: that requires opting out of a soundness guard it enables by default in
/// multithreaded programs (which this is), since determining the local UTC offset on Unix isn't
/// safely reentrant. `date` doesn't have that problem — it's what the menu bar clock itself uses.
fn format_due(epoch: u64) -> String {
    std::process::Command::new("date")
        .arg("-r")
        .arg(epoch.to_string())
        .arg("+%Y-%m-%d %H:%M %Z")
        .output()
        .ok()
        .filter(|output| output.status.success())
        .map(|output| String::from_utf8_lossy(&output.stdout).trim().to_string())
        .filter(|text| !text.is_empty())
        .unwrap_or_else(|| epoch.to_string())
}
