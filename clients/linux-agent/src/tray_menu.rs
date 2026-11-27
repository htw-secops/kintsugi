use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::mpsc::Sender;
use std::sync::{Mutex, OnceLock};

use anyhow::{Context, Result};
use ksni::blocking::{Handle, TrayMethods};
use ksni::menu::StandardItem;
use ksni::{Icon, MenuItem, ToolTip};

use crate::logging;
use crate::self_service::{self, Offer};
use crate::status::{AgentStatus, CheckInStatus, MenuAction};

const TRAY_ICON_BYTES: &[u8] = include_bytes!("../assets/tray-icon.png");
const TOOLTIP: &str = "Kintsugi Patching";

/// Sends the scheduler a `MenuAction`. Stored in a static rather than captured by the menu closure
/// because `ksni` rebuilds the menu from `&self` on every redraw, and threading a channel through
/// the tray's own state would mean `AgentStatus` updates carrying it around too — the same
/// reasoning that puts the Windows agent's sender in a static beside its window procedure.
static MENU_TX: OnceLock<Sender<MenuAction>> = OnceLock::new();

/// The live tray, once `run` has managed to register one. `None` on a host with no notification
/// area (see `run`), which every update below then silently skips.
static TRAY: Mutex<Option<Handle<KintsugiTray>>> = Mutex::new(None);

/// Status reported before `run` managed to register the tray, applied as soon as it has — see the
/// drain in `run`. The scheduler thread starts reporting within microseconds of being spawned,
/// comfortably before `spawn()` returns, so without this the first status is dropped and the menu
/// sits on its placeholder "Loading patching status" text. For a host waiting on its very first
/// policy that reads as a working agent that is merely slow, which is the exact misreading
/// `AgentStatus::WaitingForPolicy` exists to prevent.
///
/// On a host with no StatusNotifierItem host the tray never registers at all and these simply stay
/// where they are for the life of the process. That is deliberate: there is nothing to drain them
/// into, and retrying or logging per tick would turn a supported configuration into noise.
static PENDING_STATUS: Mutex<Option<AgentStatus>> = Mutex::new(None);
static PENDING_CHECK_IN: Mutex<Option<CheckInStatus>> = Mutex::new(None);

/// Who is currently controlling this host, if anybody.
///
/// **The only thing on screen that says a session is happening, which makes it part of the security
/// model rather than a nicety.** Somebody who allowed a session — or who walks up to a machine where
/// one is running — has to be able to see whose it is and end it without finding an administrator.
static REMOTE_SESSION: Mutex<Option<String>> = Mutex::new(None);

/// The titles an administrator has offered this host, for the menu's "Install software" list — see
/// `report_self_service`. A static beside `REMOTE_SESSION` for the same reason: ksni rebuilds the
/// menu from `&self`, and an update pushed before the tray registered must not be lost.
static OFFERS: Mutex<Vec<Offer>> = Mutex::new(Vec::new());

/// At most this many offered titles are listed, as in the other two agents' popups — enough for a
/// menu that still fits on a screen. shortcut: the rest are hidden; raise it with theirs.
const MAX_OFFERS: usize = 8;

/// Where a self-service install this process asked for has got to, by title name, for the menu's
/// rows — see `report_install`. Held only in this process's memory, so a restarted agent shows every
/// offer as installable again until the next check-in drops the ones now installed. Kept in step
/// with the other two agents' `tray_popup::InstallState`; Linux has no popup to put it in (see
/// `offer_item`).
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum InstallState {
    Installing,
    Installed,
    Failed,
}

static INSTALL_STATES: Mutex<Vec<(String, InstallState)>> = Mutex::new(Vec::new());

/// Set by "End Remote Session" and cleared by whoever acts on it. A flag rather than a channel for
/// the same reason both other agents use one: the click has to be meaningful whether or not a
/// session is running at that instant, and ksni rebuilds the menu from `&self` so a closure cannot
/// hold one.
static END_REMOTE_SESSION: AtomicBool = AtomicBool::new(false);

/// The three lines of menu text the icon currently shows, plus whether a patch cycle is running and
/// whether a "Check In Now" is in flight. Both action items are selectable only while neither is —
/// greyed out mid-cycle, the same way both other agents' menu items are, so a second cycle can't be
/// started on top of a running one, and greyed out during a check-in because the scheduler thread
/// serves both actions: a click then would sit in the channel and run the moment the check-in
/// finished, which from the menu looks like an item that did nothing and then, minutes later, did
/// something unasked.
struct KintsugiTray {
    check_in_line: String,
    status_line: String,
    progress_line: String,
    patching: bool,
    checking_in: bool,
    /// Greys "Patch Now" alone, leaving "Check In Now" clickable — the only state where the two
    /// diverge. See `AgentStatus::WaitingForPolicy`: there is no schedule to patch against, but a
    /// check-in is precisely what fetches one, so that item has to stay live or the state has no
    /// exit but waiting.
    awaiting_policy: bool,
}

impl KintsugiTray {
    fn actions_enabled(&self) -> bool {
        !self.patching && !self.checking_in
    }

    fn patch_now_enabled(&self) -> bool {
        self.actions_enabled() && !self.awaiting_policy
    }

    fn send(action: MenuAction) {
        match MENU_TX.get() {
            Some(sender) => {
                if let Err(err) = sender.send(action) {
                    logging::error(&format!("could not signal the scheduler thread: {err}"));
                }
            }
            None => logging::error(&format!("{action:?} clicked before the scheduler was wired up")),
        }
    }
}

impl ksni::Tray for KintsugiTray {
    // A left click opens the menu too, as either click opens the popup on macOS and Windows. Linux
    // keeps a menu where those two draw a panel (IP-976): this binary links no GUI toolkit, so that it
    // can be the headless root service too, and on Wayland nothing but the panel itself may place a
    // window beside its tray icon.
    const MENU_ON_ACTIVATE: bool = true;

    fn id(&self) -> String {
        "kintsugi-agent".into()
    }

    fn title(&self) -> String {
        TOOLTIP.into()
    }

    fn tool_tip(&self) -> ToolTip {
        ToolTip {
            title: TOOLTIP.into(),
            description: self.status_line.clone(),
            ..Default::default()
        }
    }

    fn icon_pixmap(&self) -> Vec<Icon> {
        icon().into_iter().collect()
    }

    fn menu(&self) -> Vec<MenuItem<Self>> {
        let mut items: Vec<MenuItem<Self>> = vec![
            StandardItem {
                label: self.check_in_line.clone(),
                enabled: false,
                ..Default::default()
            }
            .into(),
            StandardItem {
                label: self.status_line.clone(),
                enabled: false,
                ..Default::default()
            }
            .into(),
            StandardItem {
                label: self.progress_line.clone(),
                enabled: false,
                ..Default::default()
            }
            .into(),
            MenuItem::Separator,
        ];

        // Directly under the status lines and above "Check In Now" and "Patch Now": while a session
        // is running it is the most important thing in this menu. Absent entirely otherwise, rather
        // than greyed out — the overwhelming majority of hosts never have one.
        if let Some(requested_by) = REMOTE_SESSION.lock().ok().and_then(|held| held.clone()) {
            items.push(
                StandardItem {
                    label: format!("Remote session: {requested_by}"),
                    enabled: false,
                    ..Default::default()
                }
                .into(),
            );
            items.push(
                StandardItem {
                    label: "End Remote Session".into(),
                    activate: Box::new(|_: &mut Self| {
                        logging::info("\"End Remote Session\" clicked in the notification area");
                        END_REMOTE_SESSION.store(true, Ordering::SeqCst);
                    }),
                    ..Default::default()
                }
                .into(),
            );
            items.push(MenuItem::Separator);
        }

        // The same words as the other two agents' popups.
        items.extend([
            StandardItem {
                label: "Check in now".into(),
                enabled: self.actions_enabled(),
                activate: Box::new(|_: &mut Self| {
                    logging::info("\"Check in now\" clicked in the notification area");
                    Self::send(MenuAction::CheckInNow);
                }),
                ..Default::default()
            }
            .into(),
            StandardItem {
                label: "Install updates now".into(),
                enabled: self.patch_now_enabled(),
                activate: Box::new(|_: &mut Self| {
                    logging::info("\"Install updates now\" clicked in the notification area");
                    Self::send(MenuAction::PatchNow);
                }),
                ..Default::default()
            }
            .into(),
        ]);

        // Under the actions, in the menu itself rather than a submenu, so what is offered is seen
        // the moment the menu opens; absent rather than empty when nothing is. The list drawn is
        // recorded (`remember_shown`) so a click's index is resolved against what was on screen,
        // not against a cache the next check-in may have rewritten — the same snapshot the macOS and
        // Windows popups keep.
        let mut offers = OFFERS.lock().map(|held| held.clone()).unwrap_or_default();
        offers.truncate(MAX_OFFERS);
        self_service::remember_shown(&offers);
        if !offers.is_empty() {
            let states = INSTALL_STATES.lock().map(|states| states.clone()).unwrap_or_default();
            items.push(MenuItem::Separator);
            items.push(
                StandardItem {
                    label: "Install software".into(),
                    enabled: false,
                    ..Default::default()
                }
                .into(),
            );
            for (index, offer) in offers.iter().enumerate() {
                let state = states.iter().find(|(name, _)| *name == offer.name).map(|(_, state)| *state);
                let (label, enabled) = offer_item(offer, state);
                let name = offer.name.clone();
                items.push(
                    StandardItem {
                        label,
                        enabled,
                        activate: Box::new(move |_: &mut Self| {
                            logging::info(&format!("Install {name} (item {index}) clicked in the notification area"));
                            // Marked here, on the click, rather than when the scheduler gets to it:
                            // the scheduler may be busy, and an item that said nothing new until
                            // then would be clicked again. ksni redraws the menu after this returns.
                            set_install(&name, InstallState::Installing);
                            Self::send(MenuAction::Install(index));
                        }),
                        ..Default::default()
                    }
                    .into(),
                );
            }
        }

        items.extend([
            MenuItem::Separator,
            // Static for the life of the process — this binary's own version never changes
            // underneath it (a self-update replaces the binary on disk and restarts this process;
            // it doesn't rewrite a running one).
            StandardItem {
                label: format!("Version {}", env!("CARGO_PKG_VERSION")),
                enabled: false,
                ..Default::default()
            }
            .into(),
        ]);

        items
    }
}

/// Registers the notification-area icon and then blocks for the rest of the process's life.
/// `menu_tx` is how a click on "Check In Now" or "Patch Now" reaches the scheduler thread.
///
/// **A failure to register is not a failure of this function.** The macOS and Windows agents can
/// both take a menu bar / notification area for granted; Linux cannot. There may be no session
/// bus, no StatusNotifierItem host (a bare window manager, or GNOME without the AppIndicator
/// extension), or no graphical session at all. In every one of those cases the scheduler thread
/// this function was called alongside is still perfectly able to fetch the policy, notice a cycle
/// is due, ask the user, and patch — so this logs what happened and parks instead of returning an
/// error that would end the process and take the scheduler with it.
///
/// It never returns normally, matching `tray_menu::run` on both other agents: `main` treats this
/// call as the end of the line.
pub fn run(menu_tx: Sender<MenuAction>) -> Result<()> {
    let _ = MENU_TX.set(menu_tx);

    let tray = KintsugiTray {
        check_in_line: "Next check-in: not yet scheduled".to_string(),
        status_line: "Loading patching status\u{2026}".to_string(),
        progress_line: String::new(),
        patching: false,
        checking_in: false,
        awaiting_policy: false,
    };

    match tray.spawn() {
        Ok(handle) => {
            *TRAY.lock().expect("the tray handle mutex is never held across a panic") = Some(handle);
            logging::info("notification-area icon created");

            // Only now that TRAY is set do the two reporters below have somewhere to push to — see
            // PENDING_CHECK_IN/PENDING_STATUS. Check-in line first so the menu is never briefly
            // self-contradictory.
            if let Some(status) = PENDING_CHECK_IN.lock().ok().and_then(|mut pending| pending.take()) {
                report_check_in(status);
            }
            if let Some(status) = PENDING_STATUS.lock().ok().and_then(|mut pending| pending.take()) {
                report_status(status);
            }
        }
        Err(err) => {
            logging::warn(&format!(
                "could not show a notification-area icon ({err}) — patching continues normally; \
                 only the icon and its menu are unavailable"
            ));
        }
    }

    // Nothing to pump: `ksni` runs its own D-Bus connection on a thread of its own, and there is
    // no Cocoa/Win32 event loop that has to own this one. Parking keeps the process (and with it
    // the scheduler thread) alive, which is this function's real job.
    loop {
        std::thread::park();
    }
}

/// Shows or hides the remote-session block in the menu. Safe to call from any thread.
pub fn report_remote_session(requested_by: Option<String>) {
    if let Ok(mut held) = REMOTE_SESSION.lock() {
        *held = requested_by;
    }

    // ksni rebuilds the menu from `&self`, so it has to be told the model changed — unlike the
    // Windows agent, where the menu is constructed fresh on every click and setting the value is
    // the whole of it.
    if let Ok(handle) = TRAY.lock() {
        if let Some(handle) = handle.as_ref() {
            handle.update(|_: &mut KintsugiTray| {});
        }
    }
}

/// Replaces the menu's "Install software" list. Safe to call from any thread; the scheduler calls
/// it only when the cached list has changed.
pub fn report_self_service(offers: Vec<Offer>) {
    if let Ok(mut held) = OFFERS.lock() {
        *held = offers;
    }
    redraw();
}

/// Records how a self-service install of `name` is going, for its row in the menu. Safe to call from
/// any thread except ksni's own — a menu item's `activate` uses `set_install`, because ksni redraws
/// after it and `update` from inside it would wait on itself.
pub fn report_install(name: &str, state: InstallState) {
    set_install(name, state);
    redraw();
}

fn set_install(name: &str, state: InstallState) {
    if let Ok(mut states) = INSTALL_STATES.lock() {
        states.retain(|(existing, _)| existing != name);
        states.push((name.to_string(), state));
    }
}

/// What an offered title's row says, and whether it can be clicked: the Windows and macOS popups'
/// Install button, said in the label because a menu item has nothing else — "Installing…" and
/// "Installed" greyed, "Try again" clickable.
fn offer_item(offer: &Offer, state: Option<InstallState>) -> (String, bool) {
    match state {
        None => (offer.label(), true),
        Some(InstallState::Installing) => (format!("{} \u{2014} Installing\u{2026}", offer.label()), false),
        Some(InstallState::Installed) => (format!("{} \u{2014} Installed", offer.label()), false),
        Some(InstallState::Failed) => (format!("{} \u{2014} Try again", offer.label()), true),
    }
}

/// Tells ksni the menu's model changed, which it cannot see for itself in the statics above.
fn redraw() {
    if let Ok(handle) = TRAY.lock() {
        if let Some(handle) = handle.as_ref() {
            handle.update(|_: &mut KintsugiTray| {});
        }
    }
}

/// Whether the person at the keyboard has asked for the session to end, clearing the request.
pub fn take_end_remote_session_request() -> bool {
    END_REMOTE_SESSION.swap(false, Ordering::SeqCst)
}

/// Pushes a status update to the notification area. Safe to call from any thread — the scheduler
/// thread is the only real caller.
pub fn report_status(status: AgentStatus) {
    let (status_line, progress_line, patching) = match &status {
        AgentStatus::Idle { next_due_epoch } => (
            format!("Next patch due: {}", format_due(*next_due_epoch)),
            "Status: idle".to_string(),
            false,
        ),
        // Greyed like `Patching` (the third element), but with the progress window left closed
        // below: a prompt awaiting an answer is not progress, and a window claiming otherwise
        // would be sitting on top of the dialog it is describing.
        // Not greyed via the third element: this state greys only "Patch Now", through
        // `awaiting_policy` below, which the menu reads separately.
        AgentStatus::WaitingForPolicy => (
            "Next patch due: not scheduled yet".to_string(),
            "Status: waiting for the service's first check-in".to_string(),
            false,
        ),
        AgentStatus::AwaitingAnswer => ("Waiting for your answer".to_string(), "Status: a prompt is on screen".to_string(), true),
        AgentStatus::Patching { current, completed, total } => (
            format!("Patching: {current}"),
            if *total > 0 {
                format!("Progress: {}", crate::dialogs::progress_bar(*completed, *total))
            } else {
                "Progress: starting\u{2026}".to_string()
            },
            true,
        ),
        AgentStatus::PatchingInBackground { current } => (
            format!("Updating in the background: {current}"),
            "Status: patching an application that is not open".to_string(),
            true,
        ),
    };

    let awaiting_policy = matches!(&status, AgentStatus::WaitingForPolicy);

    match TRAY.lock().ok().and_then(|guard| guard.as_ref().cloned()) {
        Some(handle) => {
            // `update` answers `None` if the tray has since gone away, which is nothing this can
            // act on — the next report will find TRAY empty and stash instead.
            let _ = handle.update(move |tray: &mut KintsugiTray| {
                tray.status_line = status_line;
                tray.progress_line = progress_line;
                tray.patching = patching;
                tray.awaiting_policy = awaiting_policy;
            });
        }
        // Either the tray has not been registered yet — see PENDING_STATUS — or this host has no
        // notification area at all, in which case nothing will ever drain this and that is fine.
        None => {
            if let Ok(mut pending) = PENDING_STATUS.lock() {
                *pending = Some(status.clone());
            }
        }
    }

    // A window, unlike the menu, is visible without the user having to think to go looking for it
    // — opened the moment there's something to show, closed again once idle. Deliberately outside
    // the tray check: a host with no notification area may still have a display, and this is the
    // only progress the user would otherwise see.
    match &status {
        AgentStatus::Idle { .. }
        | AgentStatus::AwaitingAnswer
        | AgentStatus::WaitingForPolicy
        | AgentStatus::PatchingInBackground { .. } => crate::progress_window::hide(),
        AgentStatus::Patching { current, completed, total } => crate::progress_window::show_and_update(current, *completed, *total),
    }
}

/// Pushes the root service's check-in schedule to the menu's "Next check-in" line, and greys both
/// actions while a "Check In Now" is in flight. Safe to call from any thread, like `report_status`.
///
/// Separate from `report_status` because the two describe different processes: the patch cycle is
/// this one's, the check-in is the root service's, and either can be busy while the other is idle.
pub fn report_check_in(status: CheckInStatus) {
    let (check_in_line, checking_in) = match status {
        CheckInStatus::Scheduled { next_epoch: Some(epoch) } => (format!("Next check-in: {}", format_due(epoch)), false),
        CheckInStatus::Scheduled { next_epoch: None } => ("Next check-in: not yet scheduled".to_string(), false),
        CheckInStatus::InProgress => ("Checking in with the server\u{2026}".to_string(), true),
    };

    match TRAY.lock().ok().and_then(|guard| guard.as_ref().cloned()) {
        Some(handle) => {
            let _ = handle.update(move |tray: &mut KintsugiTray| {
                tray.check_in_line = check_in_line;
                tray.checking_in = checking_in;
            });
        }
        None => {
            if let Ok(mut pending) = PENDING_CHECK_IN.lock() {
                *pending = Some(status);
            }
        }
    }
}

/// Decodes the embedded PNG into the ARGB32 layout the StatusNotifierItem specification wants.
/// Decoded once and cached: `ksni` asks for the icon again on every redraw.
fn icon() -> Option<Icon> {
    static ICON: OnceLock<Option<Icon>> = OnceLock::new();

    ICON.get_or_init(|| match load_icon() {
        Ok(icon) => Some(icon),
        Err(err) => {
            // Not fatal: an icon-less item still shows the desktop's own fallback and its menu
            // still works.
            logging::warn(&format!("could not decode the embedded tray icon: {err:#}"));
            None
        }
    })
    .clone()
}

fn load_icon() -> Result<Icon> {
    let decoded = image::load_from_memory_with_format(TRAY_ICON_BYTES, image::ImageFormat::Png)
        .context("failed to decode the embedded tray icon")?
        .into_rgba8();
    let (width, height) = decoded.dimensions();

    let mut data = decoded.into_vec();
    // The `image` crate hands back RGBA; StatusNotifierItem wants ARGB in network byte order, so
    // each pixel's alpha moves from last to first.
    for pixel in data.chunks_exact_mut(4) {
        pixel.rotate_right(1);
    }

    Ok(Icon { width: width as i32, height: height as i32, data })
}

/// Renders `epoch` in the host's own local time (not UTC) — what the person looking at the menu
/// actually keeps their clock in. Shells out to `date` rather than using the `time` crate's own
/// local-offset support, for exactly the reason the macOS agent does: that requires opting out of
/// a soundness guard the crate enables by default in multithreaded programs (which this is),
/// since determining the local UTC offset on Unix isn't safely reentrant.
///
/// The invocation differs from the macOS one — GNU coreutils spells it `date -d @<epoch>` where
/// BSD `date` spells it `date -r <epoch>`.
fn format_due(epoch: u64) -> String {
    std::process::Command::new("date")
        .arg(format!("-d@{epoch}"))
        .arg("+%Y-%m-%d %H:%M %Z")
        .output()
        .ok()
        .filter(|output| output.status.success())
        .map(|output| String::from_utf8_lossy(&output.stdout).trim().to_string())
        .filter(|text| !text.is_empty())
        .unwrap_or_else(|| epoch.to_string())
}

#[cfg(test)]
mod tests {
    use super::*;

    /// The icon ships in the binary, so a corrupt or wrongly-formatted asset is a build-time
    /// mistake this catches rather than something a user discovers.
    #[test]
    fn the_embedded_icon_decodes_to_argb32() {
        let icon = load_icon().expect("the embedded tray icon should decode");

        assert!(icon.width > 0 && icon.height > 0);
        assert_eq!(icon.data.len(), (icon.width * icon.height * 4) as usize);
    }

    #[test]
    fn an_offer_reads_install_then_follows_the_install_and_only_a_failure_can_be_clicked_again() {
        let offer = Offer { name: "HTW Capture".to_string(), latest_version: Some("0.4.9".to_string()) };

        assert_eq!(offer_item(&offer, None), ("HTW Capture (0.4.9)".to_string(), true));
        assert_eq!(offer_item(&offer, Some(InstallState::Installing)), ("HTW Capture (0.4.9) \u{2014} Installing\u{2026}".to_string(), false));
        assert_eq!(offer_item(&offer, Some(InstallState::Installed)), ("HTW Capture (0.4.9) \u{2014} Installed".to_string(), false));
        assert_eq!(offer_item(&offer, Some(InstallState::Failed)), ("HTW Capture (0.4.9) \u{2014} Try again".to_string(), true));
    }

    #[test]
    fn format_due_falls_back_to_the_raw_epoch_rather_than_showing_nothing() {
        // Whatever `date` does with it, the result is never empty — the menu always says
        // something.
        assert!(!format_due(1_756_512_000).is_empty());
    }
}
