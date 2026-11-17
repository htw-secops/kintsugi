//! The always-visible indicator that a remote session is running, with a button to end it.
//!
//! # Why this replaced the tray menu item
//!
//! The first cut put "Remote session: …" and "End Remote Session" in the notification area, because
//! that is where the tray process lives and the tray process was doing the capture. Moving capture
//! into a SYSTEM session helper broke that: the helper has no tray icon, and giving it one would
//! mean a channel back to a medium-integrity process — reintroducing exactly the local-attack
//! surface that moving to SYSTEM removed.
//!
//! A banner is the better answer anyway, on two counts. It is **visible without being looked for**:
//! somebody walking up to a machine mid-session sees it immediately, where a tray item has to be
//! clicked to be found. And because it is owned by a SYSTEM process, UIPI stops a
//! medium-integrity process sending input to it — so user-level malware can neither click "End
//! session" nor, more importantly, close the banner to hide that a session is running.
//!
//! # Its own thread, and the desktop it lives on
//!
//! It owns a window, so its thread can never call `SetThreadDesktop` afterwards (see
//! `remote_desktop`) — which is why it cannot share the capture thread. It inherits the process's
//! startup desktop, `winsta0\default`, and stays there: while a UAC prompt is up the banner is
//! hidden, which is the right trade because the prompt is what the user is looking at and the
//! session resumes visibly the moment it is answered.
//!
//! # How it looks
//!
//! The style guide's dark toast: navy, the organisation's logo, the headline in white and who is
//! connected in light cyan, and a white "End session" pill — drawn with `popup`'s primitives so it
//! matches every other window this agent shows.

use std::sync::atomic::AtomicBool;
use std::sync::Arc;

/// A running banner. Dropping or closing it takes the window down.
pub struct Banner {
    #[cfg(windows)]
    window: platform::BannerWindow,
}

impl Banner {
    /// Takes the banner down and waits for its thread to finish.
    pub fn close(self) {
        #[cfg(windows)]
        self.window.close();
    }
}

/// Puts the banner on screen. `ended_by_user` is set when the button is pressed.
///
/// A flag rather than a channel for the same reason the other agents use one: the click has to be
/// meaningful whether or not the session loop happens to be mid-frame, and the loop is polling
/// anyway.
pub fn show(requested_by: String, ended_by_user: Arc<AtomicBool>) -> Banner {
    #[cfg(windows)]
    {
        Banner { window: platform::BannerWindow::show(requested_by, ended_by_user) }
    }

    #[cfg(not(windows))]
    {
        let _ = (requested_by, ended_by_user);
        Banner {}
    }
}

/// What the banner says: a headline, and who is connected. Split out so the wording can be tested
/// without a window.
///
/// It names the administrator, because "a remote session is running" without saying whose is not
/// something a user can act on.
pub fn banner_text(requested_by: &str) -> (&'static str, String) {
    ("Remote session in progress", format!("\u{00B7} {requested_by}"))
}

#[cfg(windows)]
mod platform {
    use std::sync::atomic::{AtomicBool, Ordering};
    use std::sync::{Arc, OnceLock};
    use std::thread::JoinHandle;

    use windows_sys::Win32::Foundation::{HWND, LPARAM, WPARAM};
    use windows_sys::Win32::Graphics::Gdi::{
        BeginPaint, BitBlt, CreateCompatibleBitmap, CreateCompatibleDC, DeleteDC, DeleteObject, EndPaint, GetDC, ReleaseDC,
        SelectObject, DT_LEFT, DT_SINGLELINE, DT_VCENTER, PAINTSTRUCT, SRCCOPY,
    };
    use windows_sys::Win32::UI::Controls::DRAWITEMSTRUCT;
    use windows_sys::Win32::UI::WindowsAndMessaging::{
        CreateWindowExW, DefWindowProcW, DestroyWindow, DispatchMessageW, GetClientRect, GetMessageW, GetSystemMetrics,
        GetWindowLongPtrW, PostMessageW, PostQuitMessage, SetWindowLongPtrW, SetWindowPos, ShowWindow, TranslateMessage,
        BS_OWNERDRAW, GWLP_USERDATA, HWND_TOPMOST, MSG, SM_CXSCREEN, SWP_NOACTIVATE, SWP_NOMOVE, SWP_NOSIZE, SW_SHOWNA, WM_CLOSE,
        WM_COMMAND, WM_DESTROY, WM_DRAWITEM, WM_ERASEBKGND, WM_NCDESTROY, WM_PAINT, WS_CHILD, WS_EX_NOACTIVATE, WS_EX_TOOLWINDOW,
        WS_EX_TOPMOST, WS_POPUP, WS_VISIBLE,
    };

    use crate::branding::Branding;
    use crate::logging;
    use crate::popup::{self, draw_button, fill_rect, rect, text, text_width, ButtonStyle, Font, Picture, Scale, CYAN_100, NAVY, WHITE};
    use crate::win32::{instance, register_class, wide, WindowClass};

    use super::banner_text;

    const CLASS_NAME: &str = "KintsugiRemoteSessionBanner";
    const END_BUTTON_ID: usize = 1;
    const END_LABEL: &str = "End session";

    /// Where the button is pressed. A static rather than window data because the window procedure is
    /// a bare `extern "system"` function, exactly as `tray_menu` keeps its sender in a static.
    static ENDED: OnceLock<Arc<AtomicBool>> = OnceLock::new();

    /// What the banner paints, owned through `GWLP_USERDATA`.
    struct State {
        scale: Scale,
        headline: (&'static str, Font),
        connected: (String, Font),
        button_font: Font,
        logo: Option<Picture>,
        /// Where the text starts, after the logo.
        text_left: i32,
    }

    pub struct BannerWindow {
        thread: Option<JoinHandle<()>>,
        window: usize,
    }

    impl BannerWindow {
        pub fn show(requested_by: String, ended_by_user: Arc<AtomicBool>) -> Self {
            let _ = ENDED.set(ended_by_user);

            let (window_tx, window_rx) = std::sync::mpsc::channel::<usize>();

            let thread = std::thread::spawn(move || {
                // No SetThreadDesktop here: a new thread starts on the process's startup desktop,
                // which the service set to winsta0\default when it launched this helper. Attaching
                // would have to happen before the window exists, and staying on Default is what is
                // wanted anyway — see the module note.
                let window = unsafe { create(&requested_by) };
                let _ = window_tx.send(window as usize);

                if window.is_null() {
                    logging::error("could not create the remote session banner; the session has no visible indicator");
                    return;
                }

                // SAFETY: a message loop over this thread's own windows. GetMessageW returns 0 on
                // WM_QUIT, which `close` posts.
                unsafe {
                    let mut message: MSG = std::mem::zeroed();
                    while GetMessageW(&mut message, std::ptr::null_mut(), 0, 0) > 0 {
                        TranslateMessage(&message);
                        DispatchMessageW(&message);
                    }
                }
            });

            // Waited for so `close` always has a handle to post to, even if the caller ends the
            // session immediately.
            let window = window_rx.recv().unwrap_or(0);

            Self { thread: Some(thread), window }
        }

        pub fn close(mut self) {
            if self.window != 0 {
                // SAFETY: a window owned by the banner thread. WM_CLOSE is posted rather than sent,
                // because sending from another thread would block on that thread's message loop.
                unsafe { PostMessageW(self.window as HWND, WM_CLOSE, 0, 0) };
            }

            if let Some(thread) = self.thread.take() {
                // Joined rather than detached: the window must be gone before the process exits, or
                // a banner can outlive the session it describes by however long teardown takes.
                let _ = thread.join();
            }
        }
    }

    impl Drop for BannerWindow {
        fn drop(&mut self) {
            // Only reached if `close` was not called — a panic on the session thread. The window
            // must still go: a banner claiming a session that has ended is worse than none.
            if self.window != 0 {
                // SAFETY: as `close`.
                unsafe { PostMessageW(self.window as HWND, WM_CLOSE, 0, 0) };
            }
        }
    }

    /// # Safety
    ///
    /// Must be called on the thread that will run the message loop for the returned window.
    unsafe fn create(requested_by: &str) -> HWND {
        // This thread only: the capture thread beside it keeps the DPI behaviour it was built
        // against — see `popup`.
        popup::enable_dpi_awareness();
        let scale = popup::system_scale();
        let class = banner_class();
        let branding = Branding::current();
        let title = wide("Remote session in progress");

        let (headline, connected) = banner_text(requested_by);
        let state = Box::new(State {
            scale,
            headline: (headline, Font::new(14.0, true, scale)),
            connected: (connected, Font::new(14.0, false, scale)),
            button_font: Font::new(13.0, true, scale),
            logo: branding.logo.as_ref().and_then(|logo| Picture::at_height(logo, scale.px(18.0))),
            text_left: 0,
        });

        // Measured against the screen, so the bar is exactly as wide as what it says.
        let screen = GetDC(std::ptr::null_mut());
        let headline_width = text_width(screen, &state.headline.1, state.headline.0);
        let connected_width = text_width(screen, &state.connected.1, &state.connected.0);
        let button_width = (text_width(screen, &state.button_font, END_LABEL) + scale.px(28.0)).max(scale.px(96.0));
        ReleaseDC(std::ptr::null_mut(), screen);

        let logo_width = state.logo.as_ref().map_or(0, |logo| logo.width + scale.px(10.0));
        let text_left = scale.px(16.0) + logo_width;
        let button_left = text_left + headline_width + scale.px(6.0) + connected_width + scale.px(16.0);
        let width = button_left + button_width + scale.px(8.0);
        let height = scale.px(48.0);

        // Centred horizontally, at the very top: out of the way of most application chrome while
        // still being the first thing in the visual field.
        let screen_width = GetSystemMetrics(SM_CXSCREEN);
        let x = ((screen_width - width) / 2).max(0);

        let window = CreateWindowExW(
            // TOPMOST so it is not covered, TOOLWINDOW so it never appears in the taskbar or
            // Alt-Tab, NOACTIVATE so it cannot steal focus from whatever the operator is driving.
            WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE,
            class.name(),
            title.as_ptr(),
            WS_POPUP | WS_VISIBLE,
            x,
            0,
            width,
            height,
            std::ptr::null_mut(),
            std::ptr::null_mut(),
            instance(),
            std::ptr::null_mut(),
        );

        if window.is_null() {
            return window;
        }

        let mut state = state;
        state.text_left = text_left;
        SetWindowLongPtrW(window, GWLP_USERDATA, Box::into_raw(state) as isize);

        // The caption is never drawn by Windows (BS_OWNERDRAW) but is what a screen reader reads.
        let button_text = wide(END_LABEL);
        let button_height = scale.px(32.0);
        CreateWindowExW(
            0,
            wide("BUTTON").as_ptr(),
            button_text.as_ptr(),
            WS_CHILD | WS_VISIBLE | BS_OWNERDRAW as u32,
            button_left,
            (height - button_height) / 2,
            button_width,
            button_height,
            window,
            END_BUTTON_ID as _,
            instance(),
            std::ptr::null_mut(),
        );

        // Re-asserted after the children exist: creating them can reorder the window relative to
        // other topmost windows.
        SetWindowPos(window, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOACTIVATE | SWP_NOMOVE | SWP_NOSIZE);
        // SHOWNA, not SHOW: showing without activating, so the remote operator's focus is untouched.
        ShowWindow(window, SW_SHOWNA);

        window
    }

    fn banner_class() -> &'static WindowClass {
        static CLASS: OnceLock<WindowClass> = OnceLock::new();
        CLASS.get_or_init(|| {
            // SAFETY: `banner_wnd_proc` is a valid window procedure and lives for the whole process.
            // No brush: WM_PAINT covers the client area.
            unsafe { register_class(CLASS_NAME, banner_wnd_proc, std::ptr::null_mut()) }
        })
    }

    unsafe fn state_of<'a>(window: HWND) -> Option<&'a State> {
        (GetWindowLongPtrW(window, GWLP_USERDATA) as *const State).as_ref()
    }

    unsafe extern "system" fn banner_wnd_proc(window: HWND, message: u32, wparam: WPARAM, lparam: LPARAM) -> isize {
        match message {
            WM_ERASEBKGND => 1,
            WM_PAINT => {
                let mut paint: PAINTSTRUCT = std::mem::zeroed();
                let hdc = BeginPaint(window, &mut paint);
                if let Some(state) = state_of(window) {
                    let mut client = rect(0, 0, 0, 0);
                    GetClientRect(window, &mut client);
                    let memory = CreateCompatibleDC(hdc);
                    let bitmap = CreateCompatibleBitmap(hdc, client.right, client.bottom);
                    let previous = SelectObject(memory, bitmap);

                    fill_rect(memory, client, NAVY);
                    if let Some(logo) = &state.logo {
                        logo.draw(memory, state.scale.px(16.0), (client.bottom - logo.height) / 2);
                    }
                    let line = DT_LEFT | DT_SINGLELINE | DT_VCENTER;
                    let headline_width = text_width(memory, &state.headline.1, state.headline.0);
                    text(memory, &state.headline.1, WHITE, state.headline.0, rect(state.text_left, 0, client.right, client.bottom), line, 0, true);
                    let connected_left = state.text_left + headline_width + state.scale.px(6.0);
                    text(memory, &state.connected.1, CYAN_100, &state.connected.0, rect(connected_left, 0, client.right, client.bottom), line, 0, true);

                    BitBlt(hdc, 0, 0, client.right, client.bottom, memory, 0, 0, SRCCOPY);
                    SelectObject(memory, previous);
                    DeleteObject(bitmap);
                    DeleteDC(memory);
                }
                EndPaint(window, &paint);
                0
            }
            WM_DRAWITEM => {
                let item = &*(lparam as *const DRAWITEMSTRUCT);
                if let Some(state) = state_of(window) {
                    fill_rect(item.hDC, item.rcItem, NAVY);
                    draw_button(item.hDC, item.rcItem, ButtonStyle::Neutral, END_LABEL, &state.button_font, false, false, state.scale);
                }
                1
            }
            WM_COMMAND if (wparam & 0xFFFF) == END_BUTTON_ID => {
                logging::info("\"End session\" pressed on the remote session banner");
                if let Some(flag) = ENDED.get() {
                    flag.store(true, Ordering::SeqCst);
                }
                0
            }
            WM_CLOSE => {
                DestroyWindow(window);
                0
            }
            WM_DESTROY => {
                PostQuitMessage(0);
                0
            }
            WM_NCDESTROY => {
                let state = GetWindowLongPtrW(window, GWLP_USERDATA) as *mut State;
                if !state.is_null() {
                    SetWindowLongPtrW(window, GWLP_USERDATA, 0);
                    drop(Box::from_raw(state));
                }
                DefWindowProcW(window, message, wparam, lparam)
            }
            _ => DefWindowProcW(window, message, wparam, lparam),
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn the_banner_names_the_administrator() {
        // "A remote session is running" without saying whose is not something a user can act on.
        let (headline, connected) = banner_text("admin@example.com");

        assert!(connected.contains("admin@example.com"), "{connected}");
        assert!(headline.to_lowercase().contains("remote session"), "{headline}");
    }

    #[test]
    fn the_banner_says_it_is_in_progress_rather_than_merely_available() {
        // It is only ever on screen while somebody is actually watching, so the wording has to be
        // present tense — a user reading "remote session" alone might take it for a capability.
        assert!(banner_text("someone").0.contains("in progress"));
    }
}
