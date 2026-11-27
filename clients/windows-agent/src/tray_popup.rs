//! The notification-area icon's popup (IP-974): what a click on the icon opens, in place of the
//! context menu it used to. A borderless panel just above the icon, in the look `popup` gives every
//! other window — the organisation's logo, Arial, pill buttons — holding the menu's three status
//! lines and its two actions, and a list of the software an administrator has offered this PC
//! (`self_service`), each with its own Install button that reports how the install went.
//!
//! # Why a window, not a menu
//!
//! A menu item can carry a label and nothing else, so an offered title could only ever be a line in a
//! submenu that said nothing once clicked. A panel can say "Installing…" on the row the person
//! clicked, and has room for whatever is offered next. It lives on the notification-area thread,
//! the same thread that owns the progress window, and is built from the same statics the menu was
//! (`tray_menu::panel`), so there is still one copy of the state.
//!
//! # How it closes
//!
//! Like the shell's own flyouts: on losing activation (a click anywhere else), on Esc, and on a
//! second click on the icon. The second click is the awkward one — pressing the mouse on the icon
//! deactivates the popup *before* the icon reports the click, so the click would open it again.
//! `toggle` ignores a click that arrives within `REOPEN_GUARD` of the popup closing that way.
//!
//! # Accessibility
//!
//! The buttons are real BUTTON windows (owner drawn, as in `popup`), so Tab, Space, Return and
//! screen readers work, and each Install button's caption names its application ("Install HTW
//! Capture") although only "Install" is drawn. The status lines are the window's own text, which a
//! screen reader announces when the popup is activated.

use std::cell::Cell;
use std::time::{Duration, Instant};

use windows_sys::Win32::Foundation::{HWND, LPARAM, POINT, RECT, WPARAM};
use windows_sys::Win32::Graphics::Gdi::{
    BeginPaint, BitBlt, CreateCompatibleBitmap, CreateCompatibleDC, DeleteDC, DeleteObject, EndPaint, GetDC, GetMonitorInfoW,
    InvalidateRect, MonitorFromPoint, ReleaseDC, SelectObject, DT_CENTER, DT_END_ELLIPSIS, DT_LEFT, DT_SINGLELINE, DT_VCENTER, HDC,
    MONITORINFO, MONITOR_DEFAULTTONEAREST, PAINTSTRUCT, SRCCOPY,
};
use windows_sys::Win32::UI::Controls::{DRAWITEMSTRUCT, ODS_FOCUS, ODS_SELECTED};
use windows_sys::Win32::UI::Input::KeyboardAndMouse::{GetFocus, SetFocus, VK_ESCAPE, VK_TAB};
use windows_sys::Win32::UI::WindowsAndMessaging::{
    CreateWindowExW, DefWindowProcW, DestroyWindow, GetClientRect, GetDlgCtrlID, GetParent, GetWindowLongPtrW, PostMessageW,
    SetClassLongPtrW, SetForegroundWindow, SetWindowLongPtrW, SetWindowPos, SetWindowTextW, ShowWindow, BS_OWNERDRAW, GCL_STYLE,
    GWLP_USERDATA, HWND_TOPMOST, MSG, SWP_NOACTIVATE, SW_SHOW, WM_ACTIVATE, WM_CLOSE, WM_COMMAND, WM_DRAWITEM, WM_ERASEBKGND,
    WM_KEYDOWN, WM_NCDESTROY, WM_PAINT, WS_CHILD, WS_CLIPCHILDREN, WS_DISABLED, WS_EX_NOPARENTNOTIFY, WS_EX_TOOLWINDOW, WS_EX_TOPMOST,
    WS_POPUP, WS_TABSTOP, WS_VISIBLE,
};

use crate::branding::Branding;
use crate::logging;
use crate::popup::{
    self, draw_button, fill_rect, fill_rounded, inflate, rect, system_scale, text, text_width, ButtonStyle, Font, Picture, Scale, INK,
    NEUTRAL_200, NEUTRAL_300, NEUTRAL_50, RING, SLATE, WHITE,
};
use crate::self_service::Offer;
use crate::status::MenuAction;
use crate::win32::{instance, register_class, wide, work_area, WindowClass};

/// At most this many offered titles are listed. shortcut: the popup does not scroll, so an
/// administrator offering more than this hides the rest — add scrolling before offering that many.
pub const MAX_OFFERS: usize = 8;

/// A click on the icon this soon after the popup closed by losing activation is the click that
/// closed it, not a request to open it again — see the module note.
const REOPEN_GUARD: Duration = Duration::from_millis(500);

const TITLE: &str = "Software updates";
const WIDTH: f32 = 360.0;
const PAD: f32 = 20.0;
const LOGO_HEIGHT: f32 = 28.0;
const LOGO_MAX_WIDTH: f32 = 120.0;
const BUTTON_HEIGHT: f32 = 34.0;
const ROW_HEIGHT: f32 = 46.0;
const FOOTER_HEIGHT: f32 = 34.0;
/// How far the popup keeps from the edges of the work area — the taskbar's edge included.
const MARGIN: f32 = 12.0;
const BUTTON_ID_BASE: usize = 100;
const CLASS_NAME: &str = "KintsugiAgentTrayPopup";
/// The class style that gives a popup the system's drop shadow. Not in every windows-sys feature
/// set under that name, so spelled out.
const CS_DROPSHADOW: isize = 0x0002_0000;

// ---------------------------------------------------------------------------------------------
// What the popup shows. Plain data, so it can be built and tested without a window.
// ---------------------------------------------------------------------------------------------

/// Where a self-service install this process asked for has got to. Held only in this process's
/// memory (`tray_menu::report_install`), so a restarted tray shows every offer as installable again
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
    pub check_in_enabled: bool,
    pub patch_enabled: bool,
    /// What is offered, in the order `self_service::remember_shown` recorded, so
    /// `MenuAction::Install(index)` resolves against exactly this list.
    pub offers: Vec<(Offer, Option<InstallState>)>,
}

/// One button: what it draws, what a screen reader calls it, and what pressing it asks for.
#[derive(Debug, Clone, PartialEq, Eq)]
struct Action {
    label: String,
    caption: String,
    style: ButtonStyle,
    enabled: bool,
    action: MenuAction,
}

impl Panel {
    /// The buttons in tab order, which is also their control ids: the two actions, then one per
    /// offered title.
    fn actions(&self) -> Vec<Action> {
        let mut actions = vec![
            Action {
                label: "Check in now".into(),
                caption: "Check in now".into(),
                style: ButtonStyle::Neutral,
                enabled: self.check_in_enabled,
                action: MenuAction::CheckInNow,
            },
            Action {
                label: "Install updates now".into(),
                caption: "Install updates now".into(),
                style: ButtonStyle::Primary,
                enabled: self.patch_enabled,
                action: MenuAction::PatchNow,
            },
        ];
        for (index, (offer, state)) in self.offers.iter().enumerate() {
            let (label, style, enabled) = match state {
                None => ("Install", ButtonStyle::Outline, true),
                Some(InstallState::Installing) => ("Installing\u{2026}", ButtonStyle::Neutral, false),
                Some(InstallState::Installed) => ("Installed", ButtonStyle::Neutral, false),
                Some(InstallState::Failed) => ("Try again", ButtonStyle::Outline, true),
            };
            actions.push(Action {
                label: label.into(),
                caption: format!("{label} {}", offer.name),
                style,
                enabled,
                action: MenuAction::Install(index),
            });
        }
        actions
    }

    /// The window's own text, which a screen reader announces when the popup opens.
    fn accessible_text(&self) -> String {
        std::iter::once(TITLE.to_string()).chain(self.lines.iter().cloned()).collect::<Vec<_>>().join(". ")
    }
}

/// Where a `width` × `height` popup goes for a click at `anchor`: centred on it and above it, kept
/// inside `work` by `margin`. A taskbar at the top puts the click above the work area, so the popup
/// drops below it instead; one at the left or right is handled by the clamp alone.
fn position_in(work: RECT, anchor: POINT, width: i32, height: i32, margin: i32) -> (i32, i32) {
    let clamp = |value: i32, low: i32, high: i32| value.min(high).max(low);
    let x = clamp(anchor.x - width / 2, work.left + margin, work.right - margin - width);
    let y = if anchor.y < work.top { work.top + margin } else { anchor.y - margin - height };
    let y = clamp(y, work.top + margin, work.bottom - margin - height);
    (x, y)
}

// ---------------------------------------------------------------------------------------------
// The window. Everything below runs on the notification-area thread.
// ---------------------------------------------------------------------------------------------

thread_local! {
    static WINDOW: Cell<HWND> = const { Cell::new(std::ptr::null_mut()) };
    static CLOSED_AT: Cell<Option<Instant>> = const { Cell::new(None) };
}

struct Fonts {
    title: Font,
    body: Font,
    heading: Font,
    small: Font,
    button: Font,
}

struct State {
    panel: Panel,
    actions: Vec<Action>,
    scale: Scale,
    fonts: Fonts,
    logo: Option<Picture>,
    buttons: Vec<HWND>,
    anchor: POINT,
}

struct Layout {
    height: i32,
    /// Each button's window rectangle: the pill plus room for its focus ring.
    buttons: Vec<RECT>,
}

impl State {
    /// Lays the panel out, painting it when `paint` is set and only measuring otherwise — one
    /// function for both, as in `popup`, so the window is never sized for a layout it doesn't draw.
    unsafe fn layout(&self, hdc: HDC, paint: bool) -> Layout {
        let s = self.scale;
        let f = &self.fonts;
        let line = s.px(1.0).max(1);
        let width = s.px(WIDTH);
        let (left, right) = (s.px(PAD), width - s.px(PAD));
        let ring = s.px(RING);
        let mut buttons = Vec::new();
        let mut y = s.px(18.0);

        // The header: the logo, then the title beside it.
        let title_left = self.logo.as_ref().map_or(left, |logo| left + logo.width + s.px(12.0));
        let title_height = text(hdc, &f.title, INK, TITLE, rect(title_left, 0, right, 1000), DT_LEFT | DT_SINGLELINE, 0, false);
        let header = title_height.max(self.logo.as_ref().map_or(0, |logo| logo.height));
        if paint {
            if let Some(logo) = &self.logo {
                logo.draw(hdc, left, y + (header - logo.height) / 2);
            }
            let top = y + (header - title_height) / 2;
            text(hdc, &f.title, INK, TITLE, rect(title_left, top, right, top + title_height), DT_LEFT | DT_SINGLELINE, 0, true);
        }
        y += header + s.px(12.0);

        for (index, value) in self.panel.lines.iter().enumerate() {
            let colour = if index == 0 { INK } else { SLATE };
            let area = rect(left, y, right, y + s.px(40.0));
            y += text(hdc, &f.body, colour, value, area, DT_LEFT | DT_SINGLELINE | DT_END_ELLIPSIS, 0, paint) + s.px(4.0);
        }

        // The two actions, side by side.
        y += s.px(10.0);
        let half = (right - left - s.px(10.0)) / 2;
        buttons.push(inflate(rect(left, y, left + half, y + s.px(BUTTON_HEIGHT)), ring));
        buttons.push(inflate(rect(right - half, y, right, y + s.px(BUTTON_HEIGHT)), ring));
        y += s.px(BUTTON_HEIGHT) + s.px(18.0);

        // The offered titles, each with its own button at the right.
        if !self.panel.offers.is_empty() {
            if paint {
                fill_rect(hdc, rect(0, y, width, y + line), NEUTRAL_200);
            }
            y += s.px(14.0);
            let heading = "Install software";
            y += text(hdc, &f.heading, INK, heading, rect(left, y, right, y + s.px(40.0)), DT_LEFT | DT_SINGLELINE, 0, paint);
            y += s.px(4.0);
            let actions = &self.actions[2..];
            for (index, ((offer, _), action)) in self.panel.offers.iter().zip(actions).enumerate() {
                if index > 0 && paint {
                    fill_rect(hdc, rect(left, y, right, y + line), NEUTRAL_200);
                }
                let row = s.px(ROW_HEIGHT);
                let pill_width = (text_width(hdc, &f.button, &action.label) + s.px(28.0)).max(s.px(96.0));
                let pill_top = y + (row - s.px(BUTTON_HEIGHT)) / 2;
                buttons.push(inflate(rect(right - pill_width, pill_top, right, pill_top + s.px(BUTTON_HEIGHT)), ring));

                // The name and the version it would install, as the submenu said it.
                let name = rect(left, y, right - pill_width - s.px(12.0), y + row);
                text(hdc, &f.body, INK, &offer.label(), name, DT_LEFT | DT_VCENTER | DT_SINGLELINE | DT_END_ELLIPSIS, 0, paint);
                y += row;
            }
            y += s.px(10.0);
        }

        // The footer: this build's version in a grey band.
        let footer = s.px(FOOTER_HEIGHT);
        if paint {
            fill_rect(hdc, rect(0, y, width, y + footer), NEUTRAL_50);
            fill_rect(hdc, rect(0, y, width, y + line), NEUTRAL_200);
            let version = format!("Version {}", env!("CARGO_PKG_VERSION"));
            text(hdc, &f.small, SLATE, &version, rect(left, y, right, y + footer), DT_LEFT | DT_VCENTER | DT_SINGLELINE, 0, true);
        }
        Layout { height: y + footer, buttons }
    }

    unsafe fn measure(&self) -> Layout {
        let screen = GetDC(std::ptr::null_mut());
        let layout = self.layout(screen, false);
        ReleaseDC(std::ptr::null_mut(), screen);
        layout
    }
}

unsafe fn state_of<'a>(window: HWND) -> Option<&'a mut State> {
    (GetWindowLongPtrW(window, GWLP_USERDATA) as *mut State).as_mut()
}

fn popup_class() -> &'static WindowClass {
    use std::sync::OnceLock;
    static CLASS: OnceLock<WindowClass> = OnceLock::new();
    // SAFETY: `wnd_proc` is a valid window procedure for the life of the process.
    CLASS.get_or_init(|| unsafe { register_class(CLASS_NAME, wnd_proc, std::ptr::null_mut()) })
}

pub fn is_open() -> bool {
    !WINDOW.with(Cell::get).is_null()
}

/// Opens the popup for a click at `anchor`, or closes it if it is open. Notification-area thread
/// only.
pub fn toggle(panel: Panel, anchor: POINT) {
    let window = WINDOW.with(Cell::get);
    if !window.is_null() {
        close();
        return;
    }
    if CLOSED_AT.with(Cell::get).is_some_and(|closed| closed.elapsed() < REOPEN_GUARD) {
        return;
    }
    // SAFETY: created, pumped and destroyed on this thread, which runs a message loop for its life.
    unsafe {
        match create(panel, anchor, &Branding::current()) {
            Some(window) => {
                WINDOW.with(|cell| cell.set(window));
                ShowWindow(window, SW_SHOW);
                // The notification area lets the process whose icon was clicked take the foreground,
                // which is what lets this window be activated, and so be closed by a click elsewhere.
                SetForegroundWindow(window);
            }
            None => logging::error("could not create the notification-area popup"),
        }
    }
}

pub fn close() {
    let window = WINDOW.with(Cell::get);
    if !window.is_null() {
        // SAFETY: the window belongs to this thread; WM_NCDESTROY frees its state.
        unsafe {
            DestroyWindow(window);
        }
    }
}

/// Replaces what an open popup shows; does nothing when it is closed.
pub fn refresh(panel: Panel) {
    let window = WINDOW.with(Cell::get);
    if window.is_null() {
        return;
    }
    // SAFETY: the window and its buttons belong to this thread.
    unsafe { update(window, panel) }
}

/// Esc closes the popup, Tab moves between its buttons and Return presses the focused one. Called
/// from the notification-area thread's message loop for every key press, since a BUTTON passes none
/// of them to its parent; answers whether the key was the popup's.
pub fn handle_key(message: &MSG) -> bool {
    let window = WINDOW.with(Cell::get);
    if window.is_null() || message.message != WM_KEYDOWN {
        return false;
    }
    // SAFETY: GetParent and the calls below only read or act on this thread's own windows.
    unsafe {
        if message.hwnd != window && GetParent(message.hwnd) != window {
            return false;
        }
        if message.wParam == VK_ESCAPE as WPARAM {
            close();
            return true;
        }
        // Focus starts on the popup itself, so no button shows a ring until the keyboard is used,
        // and `popup::handle_key`'s Tab needs a button to start from.
        if message.wParam == VK_TAB as WPARAM && GetFocus() == window {
            if let Some(first) = state_of(window).and_then(|state| state.buttons.iter().zip(&state.actions).find(|(_, action)| action.enabled)) {
                SetFocus(*first.0);
            }
            return true;
        }
        popup::handle_key(window, message.wParam)
    }
}

unsafe fn create(panel: Panel, anchor: POINT, branding: &Branding) -> Option<HWND> {
    // Already done on the notification-area thread; repeated for `--preview-popups`'s thread.
    popup::enable_dpi_awareness();
    let scale = system_scale();
    let logo = branding.logo.as_ref().and_then(|image| Picture::fitted(image, scale.px(LOGO_HEIGHT), scale.px(LOGO_MAX_WIDTH)));
    let state = Box::new(State {
        actions: panel.actions(),
        panel,
        scale,
        fonts: Fonts {
            title: Font::new(17.0, true, scale),
            body: Font::new(14.0, false, scale),
            heading: Font::new(14.0, true, scale),
            small: Font::new(12.5, false, scale),
            button: Font::new(13.5, true, scale),
        },
        logo,
        buttons: Vec::new(),
        anchor,
    });
    let layout = state.measure();
    let width = scale.px(WIDTH);
    let (x, y) = position_in(monitor_work_area(anchor), anchor, width, layout.height, scale.px(MARGIN));

    let title = wide(&state.panel.accessible_text());
    let window = CreateWindowExW(
        WS_EX_TOOLWINDOW | WS_EX_TOPMOST,
        popup_class().name(),
        title.as_ptr(),
        WS_POPUP | WS_CLIPCHILDREN,
        x,
        y,
        width,
        layout.height,
        std::ptr::null_mut(),
        std::ptr::null_mut(),
        instance(),
        std::ptr::null(),
    );
    if window.is_null() {
        return None;
    }
    // On the class rather than at registration because `register_class` sets the same style for
    // every class; only this window uses it.
    SetClassLongPtrW(window, GCL_STYLE, class_style(window) | CS_DROPSHADOW);
    let state = Box::into_raw(state);
    SetWindowLongPtrW(window, GWLP_USERDATA, state as isize);
    (*state).buttons = create_buttons(window, &(*state).actions, &layout);
    Some(window)
}

/// The class style as it stands, so the drop shadow is added to it rather than replacing it.
unsafe fn class_style(window: HWND) -> isize {
    windows_sys::Win32::UI::WindowsAndMessaging::GetClassLongPtrW(window, GCL_STYLE) as isize
}

unsafe fn create_buttons(window: HWND, actions: &[Action], layout: &Layout) -> Vec<HWND> {
    let class = wide("BUTTON");
    actions
        .iter()
        .zip(&layout.buttons)
        .enumerate()
        .map(|(index, (action, area))| {
            // The caption is never drawn (BS_OWNERDRAW) but is what a screen reader reads.
            let caption = wide(&action.caption);
            let disabled = if action.enabled { 0 } else { WS_DISABLED };
            CreateWindowExW(
                WS_EX_NOPARENTNOTIFY,
                class.as_ptr(),
                caption.as_ptr(),
                WS_CHILD | WS_VISIBLE | WS_TABSTOP | disabled | BS_OWNERDRAW as u32,
                area.left,
                area.top,
                area.right - area.left,
                area.bottom - area.top,
                window,
                (BUTTON_ID_BASE + index) as _,
                instance(),
                std::ptr::null(),
            )
        })
        .collect()
}

/// Swaps in a new panel: new buttons first, so the keyboard focus has somewhere to go, then the old
/// ones destroyed, then the window resized about the same anchor.
unsafe fn update(window: HWND, panel: Panel) {
    let focus = GetFocus();
    let focused = if !focus.is_null() && GetParent(focus) == window { Some((GetDlgCtrlID(focus) as usize).wrapping_sub(BUTTON_ID_BASE)) } else { None };
    // Each `state_of` borrow ends before any call that could send this window a message.
    let (old, layout, actions, anchor) = {
        let Some(state) = state_of(window) else { return };
        if state.panel == panel {
            return;
        }
        state.actions = panel.actions();
        state.panel = panel;
        (std::mem::take(&mut state.buttons), state.measure(), state.actions.clone(), state.anchor)
    };
    let buttons = create_buttons(window, &actions, &layout);
    if let Some(index) = focused {
        let enabled = |i: &usize| actions.get(*i).is_some_and(|action| action.enabled);
        match (index..buttons.len()).chain(0..index).find(enabled) {
            Some(next) => SetFocus(buttons[next]),
            None => SetFocus(window),
        };
    }
    for button in old {
        DestroyWindow(button);
    }
    if let Some(state) = state_of(window) {
        state.buttons = buttons;
        let title = wide(&state.panel.accessible_text());
        SetWindowTextW(window, title.as_ptr());
    }
    let width = system_scale().px(WIDTH);
    let (x, y) = position_in(monitor_work_area(anchor), anchor, width, layout.height, system_scale().px(MARGIN));
    SetWindowPos(window, HWND_TOPMOST, x, y, width, layout.height, SWP_NOACTIVATE);
    InvalidateRect(window, std::ptr::null(), 0);
}

/// The work area of the display `point` is on — the one whose icon was clicked, which on a laptop
/// with a monitor attached need not be the primary.
unsafe fn monitor_work_area(point: POINT) -> RECT {
    let monitor = MonitorFromPoint(point, MONITOR_DEFAULTTONEAREST);
    let mut info: MONITORINFO = std::mem::zeroed();
    info.cbSize = std::mem::size_of::<MONITORINFO>() as u32;
    if GetMonitorInfoW(monitor, &mut info) != 0 {
        info.rcWork
    } else {
        work_area()
    }
}

/// Paints the panel off screen and copies it in one go, so the window never shows half a frame.
unsafe fn paint(state: &State, window: HWND, hdc: HDC) {
    let mut client = rect(0, 0, 0, 0);
    GetClientRect(window, &mut client);
    let memory = CreateCompatibleDC(hdc);
    let bitmap = CreateCompatibleBitmap(hdc, client.right, client.bottom);
    let previous = SelectObject(memory, bitmap);
    fill_rect(memory, client, WHITE);
    state.layout(memory, true);
    // A hairline border: a borderless popup on a white window behind it would have no edge at all
    // where the drop shadow is faint.
    let line = state.scale.px(1.0).max(1);
    for edge in [
        rect(0, 0, client.right, line),
        rect(0, client.bottom - line, client.right, client.bottom),
        rect(0, 0, line, client.bottom),
        rect(client.right - line, 0, client.right, client.bottom),
    ] {
        fill_rect(memory, edge, NEUTRAL_300);
    }
    BitBlt(hdc, 0, 0, client.right, client.bottom, memory, 0, 0, SRCCOPY);
    SelectObject(memory, previous);
    DeleteObject(bitmap);
    DeleteDC(memory);
}

unsafe extern "system" fn wnd_proc(window: HWND, message: u32, wparam: WPARAM, lparam: LPARAM) -> isize {
    match message {
        WM_ERASEBKGND => 1,
        WM_PAINT => {
            let mut ps: PAINTSTRUCT = std::mem::zeroed();
            let hdc = BeginPaint(window, &mut ps);
            if let Some(state) = state_of(window) {
                paint(state, window, hdc);
            }
            EndPaint(window, &ps);
            0
        }
        WM_DRAWITEM => {
            let item = &*(lparam as *const DRAWITEMSTRUCT);
            let index = (item.CtlID as usize).wrapping_sub(BUTTON_ID_BASE);
            if let Some(state) = state_of(window) {
                if let Some(action) = state.actions.get(index) {
                    fill_rect(item.hDC, item.rcItem, WHITE);
                    let pill = inflate(item.rcItem, -state.scale.px(RING));
                    if action.enabled {
                        let focused = item.itemState & ODS_FOCUS != 0;
                        let pressed = item.itemState & ODS_SELECTED != 0;
                        draw_button(item.hDC, pill, action.style, &action.label, &state.fonts.button, focused, pressed, state.scale);
                    } else {
                        fill_rounded(item.hDC, pill, (pill.bottom - pill.top) / 2, NEUTRAL_200);
                        text(item.hDC, &state.fonts.button, SLATE, &action.label, pill, DT_CENTER | DT_VCENTER | DT_SINGLELINE, 0, true);
                    }
                }
            }
            1
        }
        WM_COMMAND => {
            let index = (wparam & 0xFFFF).wrapping_sub(BUTTON_ID_BASE);
            let action = state_of(window).and_then(|state| state.actions.get(index)).filter(|action| action.enabled).map(|action| action.action);
            if let Some(action) = action {
                crate::tray_menu::act(action);
                // The progress window takes over from here; an install and a check-in report back
                // into this popup instead, so it stays open for those.
                // Posted, not destroyed here, while the button that sent this is still mid-click.
                if action == MenuAction::PatchNow {
                    PostMessageW(window, WM_CLOSE, 0, 0);
                }
            }
            0
        }
        WM_ACTIVATE => {
            // WA_INACTIVE: a click somewhere else. Closed by a posted message rather than here, in
            // the middle of the activation change.
            if wparam & 0xFFFF == 0 {
                CLOSED_AT.with(|cell| cell.set(Some(Instant::now())));
                PostMessageW(window, WM_CLOSE, 0, 0);
            }
            DefWindowProcW(window, message, wparam, lparam)
        }
        WM_CLOSE => {
            DestroyWindow(window);
            0
        }
        WM_NCDESTROY => {
            let state = GetWindowLongPtrW(window, GWLP_USERDATA) as *mut State;
            SetWindowLongPtrW(window, GWLP_USERDATA, 0);
            if !state.is_null() {
                drop(Box::from_raw(state));
            }
            WINDOW.with(|cell| {
                if cell.get() == window {
                    cell.set(std::ptr::null_mut());
                }
            });
            DefWindowProcW(window, message, wparam, lparam)
        }
        _ => DefWindowProcW(window, message, wparam, lparam),
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn offer(name: &str, version: Option<&str>) -> Offer {
        Offer { name: name.into(), latest_version: version.map(str::to_string) }
    }

    fn panel(offers: Vec<(Offer, Option<InstallState>)>) -> Panel {
        Panel { lines: vec!["Status: ready".into()], check_in_enabled: true, patch_enabled: false, offers }
    }

    #[test]
    fn the_two_actions_come_first_and_follow_their_flags() {
        let actions = panel(Vec::new()).actions();

        assert_eq!(actions.len(), 2);
        assert_eq!((actions[0].action, actions[0].enabled), (MenuAction::CheckInNow, true));
        assert_eq!((actions[1].action, actions[1].enabled), (MenuAction::PatchNow, false));
    }

    #[test]
    fn each_offer_gets_a_button_whose_index_is_its_place_in_the_list() {
        let actions = panel(vec![(offer("HTW Capture", Some("0.4.9")), None), (offer("7-Zip", None), None)]).actions();

        assert_eq!(actions[2].action, MenuAction::Install(0));
        assert_eq!(actions[3].action, MenuAction::Install(1));
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

    #[test]
    fn the_accessible_text_reads_the_title_then_the_lines() {
        assert_eq!(panel(Vec::new()).accessible_text(), "Software updates. Status: ready");
    }

    fn work() -> RECT {
        rect(0, 0, 1920, 1040)
    }

    #[test]
    fn a_click_on_a_bottom_taskbar_opens_the_popup_above_it_and_on_screen() {
        // The icon near the right-hand end of a 40px taskbar under a 1040px work area.
        let (x, y) = position_in(work(), POINT { x: 1800, y: 1060 }, 360, 400, 12);

        assert_eq!(x, 1920 - 12 - 360, "clamped to the right edge");
        assert_eq!(y, 1040 - 12 - 400, "sitting on the taskbar");
    }

    #[test]
    fn a_click_on_a_top_taskbar_opens_the_popup_below_it() {
        let work = rect(0, 40, 1920, 1080);

        let (_, y) = position_in(work, POINT { x: 900, y: 20 }, 360, 400, 12);

        assert_eq!(y, 52);
    }

    #[test]
    fn a_click_inside_the_work_area_centres_the_popup_on_it_and_above_it() {
        // The overflow area's icons sit above the taskbar.
        let (x, y) = position_in(work(), POINT { x: 900, y: 1000 }, 360, 400, 12);

        assert_eq!(x, 720);
        assert_eq!(y, 1000 - 12 - 400);
    }

    #[test]
    fn a_popup_taller_than_the_screen_keeps_its_top_on_screen() {
        let (_, y) = position_in(work(), POINT { x: 900, y: 1060 }, 360, 2000, 12);

        assert_eq!(y, 12);
    }
}
