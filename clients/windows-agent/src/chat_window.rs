//! The support chat: a window on the user's screen for talking to the administrator during a screen
//! session (`ViewerInput::Chat` one way, `ChatMessage` the other).
//!
//! # Owned by the session helper, on a thread of its own
//!
//! It exists for exactly as long as a session does, so the SYSTEM session helper owns it, like the
//! banner — created hidden when the session starts, shown by the administrator's first message or
//! the banner's Chat button, hidden (never destroyed) by its close box, and destroyed when the session
//! ends. It owns a window, so `SetThreadDesktop` would refuse its thread (see `remote_desktop`): it
//! cannot share the capture thread, and stays on `winsta0\default` like the banner.
//!
//! The session loop reaches it through a channel plus a posted `WM_APP` message to wake its thread,
//! and hears back through a second channel it drains on every pass (`remote_session::serve`).
//!
//! Being a SYSTEM-owned window works in the user's favour. Their own keyboard reaches it as it reaches
//! any window, but UIPI stops a medium-integrity process sending it input — so user-level malware
//! cannot type into the chat and speak to the administrator as the user.
//!
//! # Stock edit controls inside a hand-drawn window
//!
//! The header, borders, footer and Send button are `popup`'s, so it matches every other window this
//! agent shows. The transcript and the input are ordinary EDIT controls with Arial set on them, kept
//! because they give what drawing by hand would have to rebuild: a caret, selection and copying (a
//! link the administrator sends is something the user will want to copy), scrolling, and a screen
//! reader.

/// One message in the transcript.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ChatLine {
    /// Typed here, rather than sent by the administrator.
    pub from_you: bool,
    pub text: String,
}

/// The window's title, which names who the user is talking to.
pub fn chat_title(requested_by: &str) -> String {
    format!("Chat with {requested_by}")
}

/// The transcript as the edit control shows it: each message under the name of whoever sent it,
/// "You" for the person at this keyboard, with a blank line between messages. An edit control only
/// breaks lines at CRLF, so every line ending a message carries becomes one.
pub fn transcript(lines: &[ChatLine], administrator: &str) -> String {
    lines
        .iter()
        .map(|line| {
            let sender = if line.from_you { "You" } else { administrator };
            let text = line.text.replace("\r\n", "\n").replace('\r', "\n").replace('\n', "\r\n");
            format!("{sender}\r\n{text}")
        })
        .collect::<Vec<_>>()
        .join("\r\n\r\n")
}

#[cfg(windows)]
pub use platform::ChatWindow;

#[cfg(windows)]
mod platform {
    use std::cell::RefCell;
    use std::ffi::c_void;
    use std::sync::mpsc::{self, Receiver, Sender, TryIter};
    use std::sync::OnceLock;
    use std::thread::JoinHandle;

    use windows_sys::Win32::Foundation::{HWND, LPARAM, RECT, WPARAM};
    use windows_sys::Win32::Graphics::Gdi::{
        BeginPaint, BitBlt, CreateCompatibleBitmap, CreateCompatibleDC, DeleteDC, DeleteObject, EndPaint, GetDC, GetStockObject,
        ReleaseDC, SelectObject, SetBkColor, SetTextColor, DT_END_ELLIPSIS, DT_LEFT, DT_SINGLELINE, DT_VCENTER, HDC, PAINTSTRUCT,
        SRCCOPY, WHITE_BRUSH,
    };
    use windows_sys::Win32::UI::Controls::{DRAWITEMSTRUCT, EM_LIMITTEXT, EM_SCROLLCARET, EM_SETSEL, ODS_FOCUS, ODS_SELECTED};
    use windows_sys::Win32::UI::Input::KeyboardAndMouse::{GetFocus, GetKeyState, SetFocus, VK_RETURN, VK_SHIFT, VK_TAB};
    use windows_sys::Win32::UI::WindowsAndMessaging::{
        AdjustWindowRectEx, CreateWindowExW, DefWindowProcW, DestroyWindow, DispatchMessageW, GetMessageW, GetNextDlgTabItem,
        GetWindowLongPtrW, GetWindowTextLengthW, GetWindowTextW, PostMessageW, PostQuitMessage, SendMessageW, SetForegroundWindow,
        SetWindowLongPtrW, SetWindowPos, SetWindowTextW, ShowWindow, SystemParametersInfoW, TranslateMessage, BS_OWNERDRAW,
        ES_AUTOHSCROLL, ES_AUTOVSCROLL, ES_MULTILINE, ES_READONLY, GWLP_USERDATA, HWND_TOPMOST, MSG, SPI_GETWORKAREA,
        SWP_NOACTIVATE, SWP_NOMOVE, SWP_NOSIZE, SW_HIDE, SW_SHOW, SW_SHOWNA, WM_APP, WM_CLOSE, WM_COMMAND, WM_CTLCOLOREDIT,
        WM_CTLCOLORSTATIC, WM_DESTROY, WM_DRAWITEM, WM_ERASEBKGND, WM_KEYDOWN, WM_NCDESTROY, WM_PAINT, WM_SETFOCUS, WM_SETFONT,
        WS_CAPTION, WS_CHILD, WS_CLIPCHILDREN, WS_EX_TOOLWINDOW, WS_EX_TOPMOST, WS_SYSMENU, WS_TABSTOP, WS_VISIBLE, WS_VSCROLL,
    };

    use crate::branding::Branding;
    use crate::logging;
    use crate::popup::{
        self, colorref, draw_button, fill_rect, fill_rounded, inflate, rect, text, text_width, ButtonStyle, Font, Picture, Scale, INK,
        NEUTRAL_200, NEUTRAL_300, NEUTRAL_50, RING, SLATE, WHITE,
    };
    use crate::remote_protocol::MAX_CHAT_CHARS;
    use crate::win32::{instance, register_class, wide, WindowClass};

    use super::{chat_title, transcript, ChatLine};

    const CLASS_NAME: &str = "KintsugiSupportChat";
    const SEND_BUTTON_ID: usize = 1;
    const SEND_LABEL: &str = "Send";

    /// Shown and activated with the caret in the input: the user pressed Chat on the banner.
    const WM_CHAT_SHOW: u32 = WM_APP + 1;
    /// The administrator's messages are waiting in the inbox.
    const WM_CHAT_RECEIVED: u32 = WM_APP + 2;
    /// The session is over: destroy the window, which ends the thread.
    const WM_CHAT_END: u32 = WM_APP + 3;

    const STYLE: u32 = WS_CAPTION | WS_SYSMENU | WS_CLIPCHILDREN;
    /// Topmost so a message is never hidden behind what the administrator is driving; a tool window,
    /// like the banner, so it is never in the taskbar — the banner's Chat button is how it comes back.
    const EX_STYLE: u32 = WS_EX_TOPMOST | WS_EX_TOOLWINDOW;

    // The client area, in device-independent pixels.
    const WIDTH: f32 = 360.0;
    const HEIGHT: f32 = 440.0;
    const PAD: f32 = 16.0;
    const HEADER: f32 = 40.0;
    const FIELD: f32 = 38.0;

    /// How many messages the transcript keeps. Every message rewrites the edit control's whole text,
    /// so this bounds both that and what a SYSTEM process holds for an administrator who never stops.
    const MAX_LINES: usize = 200;

    struct Layout {
        transcript_box: RECT,
        footer_top: i32,
        input_box: RECT,
        send_pill: RECT,
    }

    impl Layout {
        fn new(scale: Scale, send_width: i32) -> Self {
            let (width, height, pad) = (scale.px(WIDTH), scale.px(HEIGHT), scale.px(PAD));
            let footer_top = height - pad * 2 - scale.px(FIELD);
            let field_top = footer_top + pad;
            let field_bottom = field_top + scale.px(FIELD);
            let send_pill = rect(width - pad - send_width, field_top, width - pad, field_bottom);
            Layout {
                transcript_box: rect(pad, pad + scale.px(HEADER) + scale.px(12.0), width - pad, footer_top - scale.px(12.0)),
                footer_top,
                input_box: rect(pad, field_top, send_pill.left - scale.px(12.0), field_bottom),
                send_pill,
            }
        }
    }

    /// Everything the window needs, owned through `GWLP_USERDATA`. Only ever borrowed shared: setting
    /// an edit control's text sends this window a notification synchronously, so a `&mut` held across
    /// one would alias — the transcript's lines are in a `RefCell` for that reason.
    struct State {
        scale: Scale,
        title: String,
        administrator: String,
        eyebrow_font: Font,
        title_font: Font,
        body_font: Font,
        button_font: Font,
        logo: Option<Picture>,
        layout: Layout,
        lines: RefCell<Vec<ChatLine>>,
        transcript: HWND,
        input: HWND,
        inbox: Receiver<String>,
        outbox: Sender<String>,
    }

    impl State {
        /// Adds a message and scrolls the transcript to it.
        unsafe fn push(&self, line: ChatLine) {
            let shown = {
                let mut lines = self.lines.borrow_mut();
                if lines.len() == MAX_LINES {
                    lines.remove(0);
                }
                lines.push(line);
                wide(&transcript(&lines, &self.administrator))
            };
            SetWindowTextW(self.transcript, shown.as_ptr());
            let end = shown.len() - 1;
            SendMessageW(self.transcript, EM_SETSEL, end, end as LPARAM);
            SendMessageW(self.transcript, EM_SCROLLCARET, 0, 0);
        }

        /// Sends what is in the input, if it is more than whitespace, and clears it.
        unsafe fn send_typed(&self) {
            let mut buffer = vec![0u16; GetWindowTextLengthW(self.input).max(0) as usize + 1];
            let copied = GetWindowTextW(self.input, buffer.as_mut_ptr(), buffer.len() as i32).max(0) as usize;
            let typed = String::from_utf16_lossy(&buffer[..copied]);
            let typed = typed.trim();
            if typed.is_empty() {
                return;
            }
            let empty = wide("");
            SetWindowTextW(self.input, empty.as_ptr());
            // Shown either way: the session loop is what forwards it, and only it can know whether the
            // viewer is still there to read it.
            let _ = self.outbox.send(typed.to_string());
            self.push(ChatLine { from_you: true, text: typed.to_string() });
        }
    }

    pub struct ChatWindow {
        thread: Option<JoinHandle<()>>,
        window: usize,
        inbox: Sender<String>,
        outbox: Receiver<String>,
    }

    impl ChatWindow {
        /// Creates the window, hidden, on a thread of its own.
        pub fn open(requested_by: String) -> Self {
            let (inbox, inbox_rx) = mpsc::channel::<String>();
            let (outbox_tx, outbox) = mpsc::channel::<String>();
            let (window_tx, window_rx) = mpsc::channel::<usize>();

            let thread = std::thread::spawn(move || {
                // No SetThreadDesktop, for the banner's reason: a new thread starts on the process's
                // startup desktop, winsta0\default, which is where this belongs.
                let window = unsafe { create(requested_by, inbox_rx, outbox_tx) };
                let _ = window_tx.send(window as usize);

                if window.is_null() {
                    logging::error("could not create the support chat window; chat messages will not be shown");
                    return;
                }

                // SAFETY: a message loop over this thread's own windows. GetMessageW returns 0 on the
                // WM_QUIT that WM_DESTROY posts.
                unsafe {
                    let mut message: MSG = std::mem::zeroed();
                    while GetMessageW(&mut message, std::ptr::null_mut(), 0, 0) > 0 {
                        if message.message == WM_KEYDOWN && handle_key(window, &message) {
                            continue;
                        }
                        TranslateMessage(&message);
                        DispatchMessageW(&message);
                    }
                }
            });

            // Waited for so every later call has a window to post to.
            let window = window_rx.recv().unwrap_or(0);

            Self { thread: Some(thread), window, inbox, outbox }
        }

        /// One of the administrator's messages: added to the transcript and the window shown, without
        /// taking focus — the administrator may be typing into something else on this screen.
        pub fn receive(&self, text: String) {
            if self.inbox.send(text).is_ok() {
                self.post(WM_CHAT_RECEIVED);
            }
        }

        /// The user pressed Chat on the banner: shown, activated, and ready to type into.
        pub fn show(&self) {
            self.post(WM_CHAT_SHOW);
        }

        /// What the user has sent since the last call.
        pub fn sent(&self) -> TryIter<'_, String> {
            self.outbox.try_iter()
        }

        /// Takes the window down and waits for its thread to finish.
        pub fn close(mut self) {
            self.post(WM_CHAT_END);
            if let Some(thread) = self.thread.take() {
                // Joined, as the banner is: the window must be gone before the process exits.
                let _ = thread.join();
            }
        }

        fn post(&self, message: u32) {
            if self.window != 0 {
                // SAFETY: a window owned by the chat thread. Posted rather than sent, so this thread
                // never waits on that one's message loop.
                unsafe { PostMessageW(self.window as HWND, message, 0, 0) };
            }
        }
    }

    impl Drop for ChatWindow {
        fn drop(&mut self) {
            // Only reached without `close` — a panic on the session thread. A chat window outliving
            // its session would have the user typing to nobody.
            if self.thread.is_some() {
                self.post(WM_CHAT_END);
            }
        }
    }

    /// # Safety
    ///
    /// Must be called on the thread that will run the message loop for the returned window.
    unsafe fn create(requested_by: String, inbox: Receiver<String>, outbox: Sender<String>) -> HWND {
        // This thread only: the capture thread keeps the DPI behaviour it was built against.
        popup::enable_dpi_awareness();
        let scale = popup::system_scale();
        let branding = Branding::current();

        let button_font = Font::new(14.0, true, scale);
        let screen = GetDC(std::ptr::null_mut());
        let send_width = (text_width(screen, &button_font, SEND_LABEL) + scale.px(36.0)).max(scale.px(88.0));
        ReleaseDC(std::ptr::null_mut(), screen);

        let state = Box::new(State {
            scale,
            title: chat_title(&requested_by),
            administrator: requested_by,
            eyebrow_font: Font::new(12.0, true, scale),
            title_font: Font::new(16.0, true, scale),
            body_font: Font::new(14.0, false, scale),
            button_font,
            logo: branding.logo.as_ref().and_then(|logo| Picture::fitted(logo, scale.px(28.0), scale.px(120.0))),
            layout: Layout::new(scale, send_width),
            lines: RefCell::new(Vec::new()),
            transcript: std::ptr::null_mut(),
            input: std::ptr::null_mut(),
            inbox,
            outbox,
        });

        let mut frame = rect(0, 0, scale.px(WIDTH), scale.px(HEIGHT));
        AdjustWindowRectEx(&mut frame, STYLE, 0, EX_STYLE);
        let (width, height) = (frame.right - frame.left, frame.bottom - frame.top);

        // The bottom-right of the primary display's work area, clear of the taskbar: where a chat is
        // expected, and out of the way of whatever is being worked on in the middle.
        let mut work = rect(0, 0, 0, 0);
        SystemParametersInfoW(SPI_GETWORKAREA, 0, &mut work as *mut RECT as *mut c_void, 0);
        let margin = scale.px(PAD);
        let x = (work.right - width - margin).max(work.left);
        let y = (work.bottom - height - margin).max(work.top);

        let caption = wide(&state.title);
        let window = CreateWindowExW(
            EX_STYLE,
            chat_class().name(),
            caption.as_ptr(),
            STYLE,
            x,
            y,
            width,
            height,
            std::ptr::null_mut(),
            std::ptr::null_mut(),
            instance(),
            std::ptr::null(),
        );
        if window.is_null() {
            return window;
        }

        let mut state = state;
        let edit = wide("EDIT");
        let empty = wide("");

        let transcript_area = inflate(state.layout.transcript_box, -scale.px(10.0));
        state.transcript = CreateWindowExW(
            0,
            edit.as_ptr(),
            empty.as_ptr(),
            WS_CHILD | WS_VISIBLE | WS_TABSTOP | WS_VSCROLL | (ES_MULTILINE | ES_READONLY | ES_AUTOVSCROLL) as u32,
            transcript_area.left,
            transcript_area.top,
            transcript_area.right - transcript_area.left,
            transcript_area.bottom - transcript_area.top,
            window,
            std::ptr::null_mut(),
            instance(),
            std::ptr::null(),
        );

        let input_box = state.layout.input_box;
        let input_height = scale.px(20.0);
        state.input = CreateWindowExW(
            0,
            edit.as_ptr(),
            empty.as_ptr(),
            WS_CHILD | WS_VISIBLE | WS_TABSTOP | ES_AUTOHSCROLL as u32,
            input_box.left + scale.px(14.0),
            input_box.top + (input_box.bottom - input_box.top - input_height) / 2,
            input_box.right - input_box.left - scale.px(28.0),
            input_height,
            window,
            std::ptr::null_mut(),
            instance(),
            std::ptr::null(),
        );

        for control in [state.transcript, state.input] {
            SendMessageW(control, WM_SETFONT, state.body_font.0 as WPARAM, 0);
        }
        SendMessageW(state.input, EM_LIMITTEXT, MAX_CHAT_CHARS, 0);

        // The caption is never drawn by Windows (BS_OWNERDRAW) but is what a screen reader reads.
        let button = wide("BUTTON");
        let send_label = wide(SEND_LABEL);
        let send_area = inflate(state.layout.send_pill, scale.px(RING));
        CreateWindowExW(
            0,
            button.as_ptr(),
            send_label.as_ptr(),
            WS_CHILD | WS_VISIBLE | WS_TABSTOP | BS_OWNERDRAW as u32,
            send_area.left,
            send_area.top,
            send_area.right - send_area.left,
            send_area.bottom - send_area.top,
            window,
            SEND_BUTTON_ID as _,
            instance(),
            std::ptr::null(),
        );

        SetWindowLongPtrW(window, GWLP_USERDATA, Box::into_raw(state) as isize);
        window
    }

    fn chat_class() -> &'static WindowClass {
        static CLASS: OnceLock<WindowClass> = OnceLock::new();
        // SAFETY: `chat_wnd_proc` is a valid window procedure for the life of the process. No brush:
        // WM_PAINT covers the client area.
        CLASS.get_or_init(|| unsafe { register_class(CLASS_NAME, chat_wnd_proc, std::ptr::null_mut()) })
    }

    unsafe fn state_of<'a>(window: HWND) -> Option<&'a State> {
        (GetWindowLongPtrW(window, GWLP_USERDATA) as *const State).as_ref()
    }

    /// Return sends, from anywhere but the transcript; Tab and Shift+Tab move between the transcript,
    /// the input and Send. Done in the loop, as `popup::run_modal` does, because a plain window has no
    /// dialog manager to do it — and a single-line edit control beeps at a Return it is handed.
    unsafe fn handle_key(window: HWND, message: &MSG) -> bool {
        let Some(state) = state_of(window) else { return false };
        if message.wParam == VK_RETURN as WPARAM {
            if message.hwnd != state.transcript {
                state.send_typed();
            }
            return true;
        }
        if message.wParam == VK_TAB as WPARAM {
            let backwards = GetKeyState(VK_SHIFT as i32) < 0;
            let next = GetNextDlgTabItem(window, GetFocus(), i32::from(backwards));
            if !next.is_null() {
                SetFocus(next);
            }
            return true;
        }
        false
    }

    unsafe fn paint(state: &State, hdc: HDC, width: i32, height: i32) {
        let s = state.scale;
        let line = s.px(1.0).max(1);
        let radius = s.px(8.0);
        let (pad, right) = (s.px(PAD), width - s.px(PAD));
        fill_rect(hdc, rect(0, 0, width, height), WHITE);

        // The header: logo, then the small capitals and the title beside it, as `popup`'s cards.
        let header = rect(pad, pad, right, pad + s.px(HEADER));
        let mut text_left = pad;
        if let Some(logo) = &state.logo {
            logo.draw(hdc, pad, header.top + (s.px(HEADER) - logo.height) / 2);
            text_left += logo.width + s.px(12.0);
        }
        let line_format = DT_LEFT | DT_SINGLELINE | DT_VCENTER | DT_END_ELLIPSIS;
        let eyebrow = rect(text_left, header.top, right, header.top + s.px(16.0));
        text(hdc, &state.eyebrow_font, SLATE, "REMOTE SUPPORT", eyebrow, line_format, s.px(0.7), true);
        let title = rect(text_left, eyebrow.bottom, right, header.bottom);
        text(hdc, &state.title_font, INK, &state.title, title, line_format, 0, true);

        // The transcript's border; the edit control fills the inside.
        let transcript_box = state.layout.transcript_box;
        fill_rounded(hdc, transcript_box, radius, NEUTRAL_200);
        fill_rounded(hdc, inflate(transcript_box, -line), radius - line, WHITE);

        // The footer, as on every popup: a grey band under a hairline, holding the input and Send.
        let footer_top = state.layout.footer_top;
        fill_rect(hdc, rect(0, footer_top, width, height), NEUTRAL_50);
        fill_rect(hdc, rect(0, footer_top, width, footer_top + line), NEUTRAL_200);
        let input_box = state.layout.input_box;
        fill_rounded(hdc, input_box, radius, NEUTRAL_300);
        fill_rounded(hdc, inflate(input_box, -line), radius - line, WHITE);
    }

    unsafe extern "system" fn chat_wnd_proc(window: HWND, message: u32, wparam: WPARAM, lparam: LPARAM) -> isize {
        match message {
            WM_ERASEBKGND => 1,
            WM_PAINT => {
                let mut ps: PAINTSTRUCT = std::mem::zeroed();
                let hdc = BeginPaint(window, &mut ps);
                if let Some(state) = state_of(window) {
                    let (width, height) = (state.scale.px(WIDTH), state.scale.px(HEIGHT));
                    // Drawn off screen and copied in one go, so the window never shows half a frame.
                    let memory = CreateCompatibleDC(hdc);
                    let bitmap = CreateCompatibleBitmap(hdc, width, height);
                    let previous = SelectObject(memory, bitmap);
                    paint(state, memory, width, height);
                    BitBlt(hdc, 0, 0, width, height, memory, 0, 0, SRCCOPY);
                    SelectObject(memory, previous);
                    DeleteObject(bitmap);
                    DeleteDC(memory);
                }
                EndPaint(window, &ps);
                0
            }
            // Both edit controls white with the style guide's ink; a read-only one asks with
            // WM_CTLCOLORSTATIC, and would otherwise be dialog grey.
            WM_CTLCOLOREDIT | WM_CTLCOLORSTATIC => {
                let hdc = wparam as HDC;
                SetTextColor(hdc, colorref(INK));
                SetBkColor(hdc, colorref(WHITE));
                GetStockObject(WHITE_BRUSH) as isize
            }
            WM_DRAWITEM => {
                let item = &*(lparam as *const DRAWITEMSTRUCT);
                if let Some(state) = state_of(window) {
                    fill_rect(item.hDC, item.rcItem, NEUTRAL_50);
                    let pill = inflate(item.rcItem, -state.scale.px(RING));
                    let focused = item.itemState & ODS_FOCUS != 0;
                    let pressed = item.itemState & ODS_SELECTED != 0;
                    draw_button(item.hDC, pill, ButtonStyle::Primary, SEND_LABEL, &state.button_font, focused, pressed, state.scale);
                }
                1
            }
            // The id is checked before the state is touched: the edit controls notify through
            // WM_COMMAND too, from inside the calls that change their text.
            WM_COMMAND if (wparam & 0xFFFF) == SEND_BUTTON_ID => {
                if let Some(state) = state_of(window) {
                    state.send_typed();
                    SetFocus(state.input);
                }
                0
            }
            WM_CHAT_RECEIVED => {
                if let Some(state) = state_of(window) {
                    while let Ok(text) = state.inbox.try_recv() {
                        state.push(ChatLine { from_you: false, text });
                    }
                }
                // Shown again if the user had closed it, and raised to the top of the topmost windows,
                // without activating: focus stays wherever the administrator is working.
                ShowWindow(window, SW_SHOWNA);
                SetWindowPos(window, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOACTIVATE | SWP_NOMOVE | SWP_NOSIZE);
                0
            }
            WM_CHAT_SHOW => {
                ShowWindow(window, SW_SHOW);
                // Allowed: the click on the banner that asked for this was input to this process.
                SetForegroundWindow(window);
                if let Some(state) = state_of(window) {
                    SetFocus(state.input);
                }
                0
            }
            // The close box only hides it; the session goes on, and the banner can bring it back.
            WM_CLOSE => {
                ShowWindow(window, SW_HIDE);
                0
            }
            WM_CHAT_END => {
                DestroyWindow(window);
                0
            }
            // Windows gives a re-activated window the focus itself; the input is where it belongs.
            WM_SETFOCUS => {
                if let Some(state) = state_of(window) {
                    SetFocus(state.input);
                }
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

    fn line(from_you: bool, text: &str) -> ChatLine {
        ChatLine { from_you, text: text.to_string() }
    }

    #[test]
    fn the_title_names_who_the_user_is_talking_to() {
        assert_eq!(chat_title("admin@example.com"), "Chat with admin@example.com");
    }

    #[test]
    fn each_message_sits_under_its_sender_with_a_blank_line_between() {
        let shown = transcript(&[line(false, "Can you see the error?"), line(true, "Yes")], "admin@example.com");

        assert_eq!(shown, "admin@example.com\r\nCan you see the error?\r\n\r\nYou\r\nYes");
    }

    #[test]
    fn every_line_ending_becomes_the_crlf_an_edit_control_breaks_on() {
        // A bare \n is drawn as nothing at all in an edit control, so a two-line message would read
        // as one run-on line.
        let shown = transcript(&[line(true, "one\ntwo\r\nthree\rfour")], "admin");

        assert_eq!(shown, "You\r\none\r\ntwo\r\nthree\r\nfour");
    }

    #[test]
    fn an_empty_transcript_is_empty() {
        assert_eq!(transcript(&[], "admin"), "");
    }
}
