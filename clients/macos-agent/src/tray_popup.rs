//! The menu bar icon's popup (IP-976): what a click on the icon opens, in place of the menu it used
//! to. A borderless panel hanging just under the icon, in the look `popup.js` gives every other
//! window — the organisation's logo, Arial, pill buttons — holding the menu's status lines and its
//! two actions, the remote-session controls while a session runs, and the software an administrator
//! has offered this Mac (`self_service`), each with its own Install button that reports how the
//! install went. The Windows agent's `tray_popup.rs` is the same panel drawn with GDI; keep the
//! content, wording and sizes of the two in step.
//!
//! # Why a window, not a menu
//!
//! A menu item can carry a label and nothing else, so an offered title could only ever be a line in a
//! submenu that said nothing once clicked. A panel can say "Installing…" on the row the person
//! clicked, and has room for whatever is offered next.
//!
//! # Why in this process, when every other window is `popup.js`
//!
//! `popup.rs` gives three reasons for drawing through `osascript`, and none holds here: this panel has
//! no wall-clock deadline, it is not modal, and it asks nothing a TCC prompt could guard. What a
//! script *would* cost is the half second `osascript` takes to start, on every click of a menu bar
//! icon, where anything slower than a menu reads as broken. So it is drawn here, on the main thread
//! the menu bar already owns, like the support chat window — and with the same pieces `popup.js` uses
//! (custom `NSBox`es for every fill and pill, labels, borderless buttons over the pills), so the two
//! look alike without sharing a line.
//!
//! # How it closes
//!
//! Like the system's own menu bar panels: on a click anywhere else, on Esc, and on a second click on
//! the icon. It is a *non-activating* panel, so opening it leaves the application the person was in
//! active, as a menu does, while still taking the keyboard. Losing the keyboard is how a click
//! elsewhere closes it (`resignKeyWindow`). A click on the icon while it is open closes it through
//! `toggle`; `REOPEN_GUARD` covers the case where the panel lost the keyboard to that same click
//! first, as the Windows popup's does.
//!
//! # Accessibility
//!
//! The buttons are real `NSButton`s, so VoiceOver reads them and Full Keyboard Access reaches them,
//! and each Install button's accessibility label names its application ("Install HTW Capture")
//! although only "Install" is drawn. The status lines are real labels, read in order.

use std::cell::{Cell, RefCell};
use std::time::{Duration, Instant};

use objc2::rc::Retained;
use objc2::runtime::{AnyObject, NSObjectProtocol};
use objc2::{define_class, msg_send, sel, AllocAnyThread, MainThreadMarker, MainThreadOnly, Message};
use objc2_app_kit::{
    NSAccessibility, NSAppearance, NSAppearanceCustomization, NSAppearanceNameAqua, NSApplication, NSApplicationActivationPolicy, NSBackingStoreType, NSBox,
    NSBoxType, NSButton, NSColor, NSControl, NSEvent, NSEventMask, NSFont, NSFontAttributeName, NSForegroundColorAttributeName,
    NSImage, NSImageScaling, NSImageView, NSLineBreakMode, NSPanel, NSResponder, NSScreen, NSStatusWindowLevel, NSTextField,
    NSTitlePosition, NSView, NSWindow, NSWindowCollectionBehavior, NSWindowStyleMask,
};
use objc2_foundation::{
    NSAttributedString, NSAttributedStringKey, NSData, NSDate, NSDefaultRunLoopMode, NSDictionary, NSObject, NSPoint, NSRect, NSSize,
    NSString,
};

use crate::branding::Branding;
use crate::self_service::Offer;
use crate::status::MenuAction;

/// At most this many offered titles are listed. shortcut: the popup does not scroll, so an
/// administrator offering more than this hides the rest — add scrolling before offering that many.
pub const MAX_OFFERS: usize = 8;

/// A click on the icon this soon after the popup closed by losing the keyboard is the click that
/// closed it, not a request to open it again — see the module note.
const REOPEN_GUARD: Duration = Duration::from_millis(500);

const TITLE: &str = "Software updates";
const WIDTH: f64 = 360.0;
const PAD: f64 = 20.0;
const LOGO_HEIGHT: f64 = 28.0;
const LOGO_MAX_WIDTH: f64 = 120.0;
const BUTTON_HEIGHT: f64 = 34.0;
const ROW_HEIGHT: f64 = 46.0;
const FOOTER_HEIGHT: f64 = 34.0;
/// The panel's corners, as rounded as the system's own menu bar panels.
const RADIUS: f64 = 10.0;
/// How far under the menu bar the panel hangs, and how far it keeps from a display's sides.
const GAP: f64 = 4.0;
const MARGIN: f64 = 8.0;
/// `kVK_Escape`.
const ESCAPE_KEY_CODE: u16 = 53;

// popup.js's palette, which is the Windows agent's.
const INK: u32 = 0x0F172A;
const SLATE: u32 = 0x475569;
const WHITE: u32 = 0xFFFFFF;
const ACTION: u32 = 0x0070C0;
const NEUTRAL_50: u32 = 0xF8FAFC;
const NEUTRAL_200: u32 = 0xE2E8F0;
const NEUTRAL_300: u32 = 0xCBD5E1;
/// popup.js's warning tone — text, tint, rule — for the remote session callout.
const WARNING: (u32, u32, u32) = (0x92400E, 0xFEF3C7, 0xF59E0B);

// ---------------------------------------------------------------------------------------------
// What the popup shows. Plain data, so it can be built and tested without a window.
// ---------------------------------------------------------------------------------------------

/// Where a self-service install this process asked for has got to. Held only in this process's
/// memory (`tray_menu::report_install`), so a restarted agent shows every offer as installable again
/// until the next check-in drops the ones now installed.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum InstallState {
    Installing,
    Installed,
    Failed,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Panel {
    /// The status, progress and check-in lines, as the menu showed them; empty ones left out.
    pub lines: Vec<String>,
    /// Who is controlling this Mac while a screen session runs. The menu was the only thing on
    /// screen that said so, and the popup inherits that job — see `tray_menu::report_remote_session`.
    pub remote_session: Option<String>,
    pub check_in_enabled: bool,
    pub patch_enabled: bool,
    /// What is offered, in the order `self_service::remember_shown` recorded, so
    /// `MenuAction::Install(index)` resolves against exactly this list.
    pub offers: Vec<(Offer, Option<InstallState>)>,
}

/// What pressing a button asks for. Wider than `MenuAction` because the remote-session buttons are
/// not the scheduler's: one opens the chat window and the other raises a flag the session loop polls.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Press {
    Menu(MenuAction),
    ChatWithSupport,
    EndRemoteSession,
}

/// popup.js's three button looks.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
enum Style {
    Neutral,
    Primary,
    Outline,
}

/// One button: what it draws, what VoiceOver calls it, and what pressing it asks for.
#[derive(Debug, Clone, PartialEq, Eq)]
struct Action {
    label: String,
    caption: String,
    style: Style,
    enabled: bool,
    press: Press,
}

impl Action {
    fn new(label: &str, style: Style, enabled: bool, press: Press) -> Self {
        Action { label: label.into(), caption: label.into(), style, enabled, press }
    }
}

impl Panel {
    /// The buttons in the order they are drawn, which is also their tags: the remote-session pair
    /// while a session runs, the two actions, then one per offered title.
    fn actions(&self) -> Vec<Action> {
        let mut actions = Vec::new();
        if self.remote_session.is_some() {
            actions.push(Action::new("Chat with support", Style::Neutral, true, Press::ChatWithSupport));
            actions.push(Action::new("End remote session", Style::Outline, true, Press::EndRemoteSession));
        }
        actions.push(Action::new("Check in now", Style::Neutral, self.check_in_enabled, Press::Menu(MenuAction::CheckInNow)));
        actions.push(Action::new("Install updates now", Style::Primary, self.patch_enabled, Press::Menu(MenuAction::PatchNow)));
        for (index, (offer, state)) in self.offers.iter().enumerate() {
            let (label, style, enabled) = match state {
                None => ("Install", Style::Outline, true),
                Some(InstallState::Installing) => ("Installing\u{2026}", Style::Neutral, false),
                Some(InstallState::Installed) => ("Installed", Style::Neutral, false),
                Some(InstallState::Failed) => ("Try again", Style::Outline, true),
            };
            actions.push(Action {
                label: label.into(),
                caption: format!("{label} {}", offer.name),
                style,
                enabled,
                press: Press::Menu(MenuAction::Install(index)),
            });
        }
        actions
    }
}

/// Where the popup's top-left corner goes, in AppKit's screen coordinates (origin bottom left):
/// `GAP` under the icon whose window frame is `icon` and centred on it, kept `margin` inside
/// `visible`'s sides — the display the icon is on, which with a second display need not be the main
/// one. With no icon to hang from (`--preview-popups`), the top right of `visible`, which is where
/// menu bar icons sit.
fn top_left_in(visible: NSRect, icon: Option<NSRect>, width: f64, margin: f64) -> NSPoint {
    let low = visible.origin.x + margin;
    let high = (visible.origin.x + visible.size.width - margin - width).max(low);
    match icon {
        Some(icon) => NSPoint::new((icon.origin.x + icon.size.width / 2.0 - width / 2.0).clamp(low, high), icon.origin.y - GAP),
        None => NSPoint::new(high, visible.origin.y + visible.size.height - GAP),
    }
}

// ---------------------------------------------------------------------------------------------
// The window. Everything below runs on the main thread.
// ---------------------------------------------------------------------------------------------

define_class!(
    // SAFETY:
    // - NSPanel imposes no subclassing requirements beyond NSWindow's, which are none.
    // - This class does not implement Drop.
    // - MainThreadOnly, as every window is.
    #[unsafe(super(NSPanel, NSWindow, NSResponder, NSObject))]
    #[thread_kind = MainThreadOnly]
    #[name = "KintsugiTrayPopupPanel"]
    struct PopupPanel;

    unsafe impl NSObjectProtocol for PopupPanel {}

    impl PopupPanel {
        // A borderless window refuses the keyboard unless it says otherwise, and the popup needs it:
        // that is how Esc reaches it, and losing it is how a click elsewhere closes it.
        #[unsafe(method(canBecomeKeyWindow))]
        fn can_become_key_window(&self) -> bool {
            true
        }

        #[unsafe(method(resignKeyWindow))]
        fn resign_key_window(&self) {
            // SAFETY: NSWindow implements resignKeyWindow, which takes and returns nothing.
            unsafe {
                let _: () = msg_send![super(self), resignKeyWindow];
            }
            CLOSED_AT.with(|cell| cell.set(Some(Instant::now())));
            close_later();
        }

        // Every key a button does not use arrives here, Esc included.
        #[unsafe(method(keyDown:))]
        fn key_down(&self, event: &NSEvent) {
            if event.keyCode() == ESCAPE_KEY_CODE {
                close_later();
            } else {
                // SAFETY: NSResponder implements keyDown:, which takes an event and returns nothing.
                unsafe {
                    let _: () = msg_send![super(self), keyDown: event];
                }
            }
        }
    }
);

define_class!(
    // SAFETY:
    // - NSObject imposes no subclassing requirements.
    // - This class does not implement Drop.
    // - MainThreadOnly: it is only ever the target of controls, which AppKit messages on the main
    //   thread.
    #[unsafe(super(NSObject))]
    #[thread_kind = MainThreadOnly]
    #[name = "KintsugiTrayPopupTarget"]
    struct PressTarget;

    unsafe impl NSObjectProtocol for PressTarget {}

    impl PressTarget {
        // Every button's action; its tag is its index in `Open::actions`.
        #[unsafe(method(press:))]
        fn press(&self, sender: Option<&NSControl>) {
            if let Some(sender) = sender {
                pressed(sender.tag() as usize);
            }
        }
    }
);

impl PressTarget {
    fn new(mtm: MainThreadMarker) -> Retained<Self> {
        let this = mtm.alloc::<Self>().set_ivars(());
        // SAFETY: NSObject's init, on a freshly allocated instance.
        unsafe { msg_send![super(this), init] }
    }
}

/// The popup while it is on screen.
struct Open {
    panel: Retained<PopupPanel>,
    /// Held because a control does not retain its target.
    target: Retained<PressTarget>,
    data: Panel,
    actions: Vec<Action>,
    buttons: Vec<Retained<NSButton>>,
}

thread_local! {
    // Only ever touched from the main thread, like `tray_menu::MENU_STATE`.
    static POPUP: RefCell<Option<Open>> = const { RefCell::new(None) };
    static CLOSED_AT: Cell<Option<Instant>> = const { Cell::new(None) };
}

pub fn is_open() -> bool {
    POPUP.with(|cell| cell.borrow().is_some())
}

/// Opens the popup under `icon` (the status item's window frame) on the display whose visible
/// frame is `visible`, or closes it if it is open. Main thread only.
pub fn toggle(data: Panel, icon: Option<NSRect>, visible: NSRect) {
    let Some(mtm) = MainThreadMarker::new() else { return };
    if is_open() {
        close();
        return;
    }
    if CLOSED_AT.with(Cell::get).is_some_and(|closed| closed.elapsed() < REOPEN_GUARD) {
        return;
    }

    let panel = create_panel(mtm);
    let target = PressTarget::new(mtm);
    let actions = data.actions();
    let (content, buttons, height) = build(mtm, &data, &actions, &target, &Branding::current());
    panel.setContentView(Some(&content));
    panel.setContentSize(NSSize::new(WIDTH, height));
    panel.setFrameTopLeftPoint(top_left_in(visible, icon, WIDTH, MARGIN));

    // Recorded before it is shown, so anything showing it sets off finds it open.
    POPUP.with(|cell| *cell.borrow_mut() = Some(Open { panel: panel.clone(), target, data, actions, buttons }));
    panel.makeKeyAndOrderFront(None);
    panel.invalidateShadow();
}

pub fn close() {
    // Taken out before `orderOut`, which makes the panel resign the keyboard and so re-enters here.
    if let Some(open) = POPUP.with(|cell| cell.borrow_mut().take()) {
        open.panel.orderOut(None);
    }
}

/// Closes the popup once AppKit has finished whatever it is in the middle of — a key change, or the
/// button press that asked for it — rather than from inside it.
fn close_later() {
    dispatch::Queue::main().exec_async(close);
}

/// Replaces what an open popup shows; does nothing when it is closed or nothing has changed.
pub fn refresh(data: Panel) {
    let Some(mtm) = MainThreadMarker::new() else { return };
    // Each borrow ends before AppKit is called, since a key change can re-enter `close`.
    let Some((panel, target, focused)) = POPUP.with(|cell| {
        let borrowed = cell.borrow();
        let open = borrowed.as_ref().filter(|open| open.data != data)?;
        let responder = open.panel.firstResponder();
        let focused = responder.and_then(|responder| {
            open.buttons.iter().position(|button| std::ptr::eq(Retained::as_ptr(button).cast::<AnyObject>(), Retained::as_ptr(&responder).cast()))
        });
        Some((open.panel.clone(), open.target.clone(), focused))
    }) else {
        return;
    };

    let actions = data.actions();
    let (content, buttons, height) = build(mtm, &data, &actions, &target, &Branding::current());
    // About the top edge, which hangs from the menu bar, rather than AppKit's bottom-left origin.
    let frame = panel.frame();
    let top_left = NSPoint::new(frame.origin.x, frame.origin.y + frame.size.height);
    panel.setContentView(Some(&content));
    panel.setContentSize(NSSize::new(WIDTH, height));
    panel.setFrameTopLeftPoint(top_left);
    panel.invalidateShadow();

    // The keyboard stays where it was, or moves on to the next button that can take it — the one it
    // was on is greyed while its install runs.
    if let Some(index) = focused {
        let enabled = |i: &usize| actions.get(*i).is_some_and(|action| action.enabled);
        if let Some(next) = (index..buttons.len()).chain(0..index).find(enabled) {
            let button: &NSResponder = &buttons[next];
            panel.makeFirstResponder(Some(button));
        }
    }
    POPUP.with(|cell| {
        if let Some(open) = cell.borrow_mut().as_mut() {
            open.data = data;
            open.actions = actions;
            open.buttons = buttons;
        }
    });
}

/// A button was pressed. Acted on once the press is over: acting can redraw the popup, which would
/// otherwise remove the very button AppKit is still in the middle of clicking.
fn pressed(index: usize) {
    let press = POPUP.with(|cell| cell.borrow().as_ref().and_then(|open| open.actions.get(index)).filter(|action| action.enabled).map(|action| action.press));
    let Some(press) = press else { return };
    dispatch::Queue::main().exec_async(move || {
        crate::tray_menu::act(press);
        // The progress window and the chat window take over from these two; an install and a
        // check-in report back into this popup instead, so it stays open for those.
        if matches!(press, Press::Menu(MenuAction::PatchNow) | Press::ChatWithSupport) {
            close();
        }
    });
}

/// `--preview-popups`' first window: the popup with `data`, at the top right of the main display,
/// until it is closed or 20 seconds pass. Its buttons reach `tray_menu::act`, which has no scheduler
/// to tell in a preview and only logs.
pub fn preview(data: Panel) {
    let Some(mtm) = MainThreadMarker::new() else { return };
    let app = NSApplication::sharedApplication(mtm);
    app.setActivationPolicy(NSApplicationActivationPolicy::Accessory);
    app.finishLaunching();
    let Some(visible) = NSScreen::mainScreen(mtm).map(|screen| screen.visibleFrame()) else { return };
    toggle(data, None, visible);

    // A hand-run event loop rather than `app.run()`, which would never come back to show the rest.
    let deadline = Instant::now() + Duration::from_secs(20);
    while is_open() && Instant::now() < deadline {
        let until = NSDate::dateWithTimeIntervalSinceNow(0.1);
        // SAFETY: the default run loop mode, read once; the event is dispatched straight back.
        let event = unsafe { app.nextEventMatchingMask_untilDate_inMode_dequeue(NSEventMask::Any, Some(&until), NSDefaultRunLoopMode, true) };
        if let Some(event) = event {
            app.sendEvent(&event);
        }
    }
    close();
}

fn create_panel(mtm: MainThreadMarker) -> Retained<PopupPanel> {
    let this = mtm.alloc::<PopupPanel>().set_ivars(());
    let frame = NSRect::new(NSPoint::new(0.0, 0.0), NSSize::new(WIDTH, 100.0));
    // Non-activating from its creation, which is the only time AppKit honours it: the application
    // the person was using stays active, as it does when a menu opens.
    let style = NSWindowStyleMask::Borderless | NSWindowStyleMask::NonactivatingPanel;
    // SAFETY: NSPanel's designated initializer, on a freshly allocated instance, on the main thread.
    let panel: Retained<PopupPanel> = unsafe {
        msg_send![super(this), initWithContentRect: frame, styleMask: style, backing: NSBackingStoreType::Buffered, defer: false]
    };
    // SAFETY: false is the safe direction: `POPUP` owns the panel and drops it after `orderOut`.
    unsafe { panel.setReleasedWhenClosed(false) };
    // Clear and shadowed, so the rounded card drawn in it is the window's shape.
    panel.setOpaque(false);
    panel.setBackgroundColor(Some(&NSColor::clearColor()));
    panel.setHasShadow(true);
    panel.setLevel(NSStatusWindowLevel);
    // An NSPanel hides itself whenever its application deactivates unless told otherwise — and this
    // application is never the active one while the popup is up.
    panel.setHidesOnDeactivate(false);
    panel.setCollectionBehavior(NSWindowCollectionBehavior::CanJoinAllSpaces | NSWindowCollectionBehavior::FullScreenAuxiliary);
    // Never drawn, but what VoiceOver calls the window.
    panel.setTitle(&NSString::from_str(TITLE));
    // Light whatever the Mac's own setting, because every colour drawn is: as popup.js does.
    // SAFETY: an AppKit constant, read once.
    if let Some(aqua) = NSAppearance::appearanceNamed(unsafe { NSAppearanceNameAqua }) {
        panel.setAppearance(Some(&aqua));
    }
    panel
}

fn colour(hex: u32) -> Retained<NSColor> {
    let channel = |shift: u32| f64::from((hex >> shift) & 0xFF) / 255.0;
    NSColor::colorWithSRGBRed_green_blue_alpha(channel(16), channel(8), channel(0), 1.0)
}

fn font(size: f64, bold: bool) -> Retained<NSFont> {
    let name = NSString::from_str(if bold { "Arial-BoldMT" } else { "ArialMT" });
    NSFont::fontWithName_size(&name, size).unwrap_or_else(|| if bold { NSFont::boldSystemFontOfSize(size) } else { NSFont::systemFontOfSize(size) })
}

/// A one-line label, cut short with an ellipsis rather than wrapped, sized to its text.
fn label(mtm: MainThreadMarker, text: &str, font: &NSFont, hex: u32) -> Retained<NSTextField> {
    let field = NSTextField::labelWithString(&NSString::from_str(text), mtm);
    field.setFont(Some(font));
    field.setTextColor(Some(&colour(hex)));
    field.setLineBreakMode(NSLineBreakMode::ByTruncatingTail);
    field.sizeToFit();
    field
}

/// A filled, optionally bordered, rounded rectangle — `NSBox`'s custom style, as in popup.js, so no
/// layer is needed.
fn filled(mtm: MainThreadMarker, fill: u32, border: Option<u32>, radius: f64) -> Retained<NSBox> {
    let filled = NSBox::initWithFrame(mtm.alloc(), NSRect::ZERO);
    filled.setBoxType(NSBoxType::Custom);
    filled.setTitlePosition(NSTitlePosition::NoTitle);
    filled.setFillColor(&colour(fill));
    filled.setBorderColor(&colour(border.unwrap_or(fill)));
    filled.setBorderWidth(if border.is_some() { 1.0 } else { 0.0 });
    filled.setCornerRadius(radius);
    filled
}

/// `text` in `font` and `hex`, for a button title — the one place a plain title cannot say its colour.
fn styled(text: &str, font: &NSFont, hex: u32) -> Retained<NSAttributedString> {
    let colour = colour(hex);
    // SAFETY: two AppKit constants, read once.
    let keys: [&NSAttributedStringKey; 2] = unsafe { [NSFontAttributeName, NSForegroundColorAttributeName] };
    let objects: [&AnyObject; 2] = [font, &colour];
    let attributes = NSDictionary::from_slices(&keys, &objects);
    // SAFETY: both values are of the types their keys document.
    unsafe { NSAttributedString::initWithString_attributes(NSAttributedString::alloc(), &NSString::from_str(text), Some(&attributes)) }
}

/// Views laid out from the top, as the panel is read, and turned the right way up at the end.
struct Placed {
    views: Vec<(Retained<NSView>, NSRect)>,
}

impl Placed {
    fn place(&mut self, view: &impl AsRef<NSView>, x: f64, y: f64, width: f64, height: f64) {
        self.views.push((view.as_ref().retain(), NSRect::new(NSPoint::new(x, y), NSSize::new(width, height))));
    }
}

/// Lays the panel out and returns its content view, its buttons in `actions` order, and its height.
fn build(
    mtm: MainThreadMarker,
    data: &Panel,
    actions: &[Action],
    target: &PressTarget,
    branding: &Branding,
) -> (Retained<NSView>, Vec<Retained<NSButton>>, f64) {
    let mut placed = Placed { views: Vec::new() };
    let mut buttons = Vec::new();
    let (left, right) = (PAD, WIDTH - PAD);
    let body = font(14.0, false);
    let bold = font(14.0, true);
    let button_font = font(13.5, true);
    let mut y = 18.0;

    let mut pill = |placed: &mut Placed, action: &Action, index: usize, x: f64, y: f64, width: f64| {
        let (fill, border, text) = match (action.enabled, action.style) {
            (false, _) => (NEUTRAL_200, None, SLATE),
            (true, Style::Primary) => (ACTION, None, WHITE),
            (true, Style::Outline) => (WHITE, Some(ACTION), ACTION),
            (true, Style::Neutral) => (WHITE, Some(NEUTRAL_300), SLATE),
        };
        placed.place(&filled(mtm, fill, border, BUTTON_HEIGHT / 2.0), x, y, width, BUTTON_HEIGHT);
        // A borderless button over the pill rather than a drawn one, so it keeps Space, Return and
        // VoiceOver, as popup.js's buttons do.
        let button = NSButton::initWithFrame(mtm.alloc(), NSRect::ZERO);
        button.setBordered(false);
        button.setAttributedTitle(&styled(&action.label, &button_font, text));
        button.setTag(index as isize);
        button.setEnabled(action.enabled);
        button.setAccessibilityLabel(Some(&NSString::from_str(&action.caption)));
        // SAFETY: `press:` is defined on `target`, which `Open` keeps alive as long as the buttons.
        unsafe {
            button.setTarget(Some(target));
            button.setAction(Some(sel!(press:)));
        }
        placed.place(&button, x, y, width, BUTTON_HEIGHT);
        buttons.push(button);
    };

    // The header: the logo, then the title beside it.
    let logo = branding.logo.as_ref().zip(branding.logo_png()).and_then(|(decoded, png)| {
        let image = NSImage::initWithData(NSImage::alloc(), &NSData::with_bytes(&png))?;
        let width = (LOGO_HEIGHT * f64::from(decoded.width()) / f64::from(decoded.height().max(1))).min(LOGO_MAX_WIDTH);
        let view = NSImageView::imageViewWithImage(&image, mtm);
        view.setImageScaling(NSImageScaling::ScaleProportionallyUpOrDown);
        Some((view, width))
    });
    let title = label(mtm, TITLE, &font(17.0, true), INK);
    let title_height = title.frame().size.height;
    let header = title_height.max(if logo.is_some() { LOGO_HEIGHT } else { 0.0 });
    let title_left = logo.as_ref().map_or(left, |(_, width)| left + width + 12.0);
    if let Some((view, width)) = &logo {
        placed.place(view, left, y + (header - LOGO_HEIGHT) / 2.0, *width, LOGO_HEIGHT);
    }
    placed.place(&title, title_left, y + (header - title_height) / 2.0, right - title_left, title_height);
    y += header + 12.0;

    for (index, line) in data.lines.iter().enumerate() {
        let field = label(mtm, line, &body, if index == 0 { INK } else { SLATE });
        let height = field.frame().size.height;
        placed.place(&field, left, y, right - left, height);
        y += height + 4.0;
    }

    let half = (right - left - 10.0) / 2.0;
    let mut next = 0;
    // While a session runs: who it is, in the warning tone, and the two buttons that answer it.
    if let Some(requested_by) = &data.remote_session {
        y += 10.0;
        let field = label(mtm, &format!("Remote session: {requested_by}"), &bold, WARNING.0);
        let height = field.frame().size.height;
        placed.place(&filled(mtm, WARNING.1, Some(WARNING.2), 8.0), left, y, right - left, height + 20.0);
        placed.place(&field, left + 12.0, y + 10.0, right - left - 24.0, height);
        y += height + 30.0;
        pill(&mut placed, &actions[0], 0, left, y, half);
        pill(&mut placed, &actions[1], 1, right - half, y, half);
        y += BUTTON_HEIGHT;
        next = 2;
    }

    // The two actions, side by side.
    y += 10.0;
    pill(&mut placed, &actions[next], next, left, y, half);
    pill(&mut placed, &actions[next + 1], next + 1, right - half, y, half);
    y += BUTTON_HEIGHT + 18.0;
    next += 2;

    // The offered titles, each with its own button at the right. Rules stop a point short of each
    // side, inside the card's border.
    if !data.offers.is_empty() {
        placed.place(&filled(mtm, NEUTRAL_200, None, 0.0), 1.0, y, WIDTH - 2.0, 1.0);
        y += 14.0;
        let heading = label(mtm, "Install software", &bold, INK);
        let height = heading.frame().size.height;
        placed.place(&heading, left, y, right - left, height);
        y += height + 4.0;
        for (index, (offer, _)) in data.offers.iter().enumerate() {
            let Some(action) = actions.get(next + index) else { break };
            if index > 0 {
                placed.place(&filled(mtm, NEUTRAL_200, None, 0.0), left, y, right - left, 1.0);
            }
            let pill_width = (label(mtm, &action.label, &button_font, INK).frame().size.width + 28.0).max(96.0);
            pill(&mut placed, action, next + index, right - pill_width, y + (ROW_HEIGHT - BUTTON_HEIGHT) / 2.0, pill_width);

            // The name and the version it would install, as the submenu said it.
            let name = label(mtm, &offer.label(), &body, INK);
            let height = name.frame().size.height;
            placed.place(&name, left, y + (ROW_HEIGHT - height) / 2.0, right - pill_width - 12.0 - left, height);
            y += ROW_HEIGHT;
        }
        y += 10.0;
    }

    // The footer: this build's version in a grey band. The band's lower corners follow the card's;
    // a square strip over its top squares off the upper ones.
    let footer_top = y;
    let height = footer_top + FOOTER_HEIGHT;
    placed.place(&filled(mtm, NEUTRAL_50, None, RADIUS - 1.0), 1.0, footer_top, WIDTH - 2.0, FOOTER_HEIGHT - 1.0);
    placed.place(&filled(mtm, NEUTRAL_50, None, 0.0), 1.0, footer_top, WIDTH - 2.0, RADIUS);
    placed.place(&filled(mtm, NEUTRAL_200, None, 0.0), 1.0, footer_top, WIDTH - 2.0, 1.0);
    let version = label(mtm, &format!("Version {}", env!("CARGO_PKG_VERSION")), &font(12.5, false), SLATE);
    let version_height = version.frame().size.height;
    placed.place(&version, left, footer_top + (FOOTER_HEIGHT - version_height) / 2.0, right - left, version_height);

    // The card itself, under everything: white, with a hairline border, in the window's clear
    // background.
    let card = filled(mtm, WHITE, Some(NEUTRAL_300), RADIUS);
    let card: &NSView = &card;
    placed.views.insert(0, (card.retain(), NSRect::new(NSPoint::new(0.0, 0.0), NSSize::new(WIDTH, height))));

    let content = NSView::initWithFrame(mtm.alloc(), NSRect::new(NSPoint::new(0.0, 0.0), NSSize::new(WIDTH, height)));
    for (view, area) in placed.views {
        // AppKit's origin is the bottom left; everything above was laid out from the top.
        view.setFrame(NSRect::new(NSPoint::new(area.origin.x, height - area.origin.y - area.size.height), area.size));
        content.addSubview(&view);
    }
    (content, buttons, height)
}

#[cfg(test)]
mod tests {
    use super::*;

    fn offer(name: &str, version: Option<&str>) -> Offer {
        Offer { name: name.into(), latest_version: version.map(str::to_string) }
    }

    fn panel(offers: Vec<(Offer, Option<InstallState>)>) -> Panel {
        Panel { lines: vec!["Status: idle".into()], remote_session: None, check_in_enabled: true, patch_enabled: false, offers }
    }

    #[test]
    fn the_two_actions_come_first_and_follow_their_flags() {
        let actions = panel(Vec::new()).actions();

        assert_eq!(actions.len(), 2);
        assert_eq!((actions[0].press, actions[0].enabled), (Press::Menu(MenuAction::CheckInNow), true));
        assert_eq!((actions[1].press, actions[1].enabled), (Press::Menu(MenuAction::PatchNow), false));
    }

    #[test]
    fn a_remote_session_puts_its_two_buttons_above_the_actions() {
        let mut with_session = panel(vec![(offer("HTW Capture", None), None)]);
        with_session.remote_session = Some("Alex Admin".into());

        let actions = with_session.actions();

        assert_eq!(actions[0].press, Press::ChatWithSupport);
        assert_eq!(actions[1].press, Press::EndRemoteSession);
        assert_eq!(actions[2].press, Press::Menu(MenuAction::CheckInNow));
        // An offer's index is its place in the offers, wherever its button sits.
        assert_eq!(actions[4].press, Press::Menu(MenuAction::Install(0)));
    }

    #[test]
    fn each_offer_gets_a_button_whose_index_is_its_place_in_the_list() {
        let actions = panel(vec![(offer("HTW Capture", Some("0.4.9")), None), (offer("7-Zip", None), None)]).actions();

        assert_eq!(actions[2].press, Press::Menu(MenuAction::Install(0)));
        assert_eq!(actions[3].press, Press::Menu(MenuAction::Install(1)));
        // Drawn as "Install", read out with the application's name.
        assert_eq!(actions[2].label, "Install");
        assert_eq!(actions[2].caption, "Install HTW Capture");
    }

    #[test]
    fn an_install_in_flight_or_done_cannot_be_pressed_again_but_a_failed_one_can() {
        let actions = panel(vec![
            (offer("A", None), Some(InstallState::Installing)),
            (offer("B", None), Some(InstallState::Installed)),
            (offer("C", None), Some(InstallState::Failed)),
        ])
        .actions();

        assert_eq!((actions[2].label.as_str(), actions[2].enabled), ("Installing\u{2026}", false));
        assert_eq!((actions[3].label.as_str(), actions[3].enabled), ("Installed", false));
        assert_eq!((actions[4].label.as_str(), actions[4].enabled), ("Try again", true));
        assert_eq!(actions[4].caption, "Try again C");
    }

    fn rect(x: f64, y: f64, width: f64, height: f64) -> NSRect {
        NSRect::new(NSPoint::new(x, y), NSSize::new(width, height))
    }

    /// A 1440×900 display under a 25-point menu bar, its Dock at the bottom.
    fn visible() -> NSRect {
        rect(0.0, 80.0, 1440.0, 795.0)
    }

    #[test]
    fn the_popup_hangs_under_the_icon_centred_on_it() {
        let top_left = top_left_in(visible(), Some(rect(800.0, 875.0, 24.0, 25.0)), 360.0, 8.0);

        assert_eq!(top_left.x, 812.0 - 180.0);
        assert_eq!(top_left.y, 875.0 - GAP, "its top just under the menu bar");
    }

    #[test]
    fn an_icon_near_the_right_edge_keeps_the_popup_on_screen() {
        let top_left = top_left_in(visible(), Some(rect(1400.0, 875.0, 24.0, 25.0)), 360.0, 8.0);

        assert_eq!(top_left.x, 1440.0 - 8.0 - 360.0);
    }

    #[test]
    fn a_second_display_to_the_left_keeps_the_popup_on_that_display() {
        let left_display = rect(-1920.0, 0.0, 1920.0, 1055.0);

        let top_left = top_left_in(left_display, Some(rect(-1910.0, 1055.0, 24.0, 25.0)), 360.0, 8.0);

        assert_eq!(top_left.x, -1920.0 + 8.0);
    }

    #[test]
    fn with_no_icon_the_popup_sits_at_the_top_right() {
        let top_left = top_left_in(visible(), None, 360.0, 8.0);

        assert_eq!((top_left.x, top_left.y), (1440.0 - 8.0 - 360.0, 875.0 - GAP));
    }
}
