//! The support chat window: the host user's end of `ViewerInput::Chat` during a screen session.
//!
//! Main-thread only, like every AppKit object, and held in a thread-local for the same reason as
//! `progress_window::WINDOW` and `tray_menu::MENU_STATE`. The session thread in `remote_control`
//! never touches it: every public function here queues its work onto the main thread with
//! `dispatch`, the way `tray_menu::report_remote_session` does. What the user types travels the other
//! way on the channel handed to [`begin_session`], which `remote_control::run_session` drains on
//! every pass and sends as a `ChatMessage` — the session socket has one owner, and it is that loop.

use std::cell::RefCell;
use std::sync::mpsc::Sender;

use objc2::rc::Retained;
use objc2::runtime::AnyObject;
use objc2::{define_class, msg_send, sel, MainThreadMarker, MainThreadOnly};
use objc2_app_kit::{
    NSApplication, NSBackingStoreType, NSButton, NSFloatingWindowLevel, NSFont, NSResponder, NSScreen, NSTextField,
    NSTextView, NSView, NSWindow, NSWindowStyleMask,
};
use objc2_foundation::{NSObject, NSObjectProtocol, NSPoint, NSRange, NSRect, NSSize, NSString};

use crate::logging;
use crate::remote_protocol::MAX_CHAT_CHARS;

const WIDTH: f64 = 360.0;
const HEIGHT: f64 = 320.0;
const MARGIN: f64 = 12.0;
const BUTTON_WIDTH: f64 = 80.0;
/// Between the window and the edges of the screen's visible frame.
const SCREEN_MARGIN: f64 = 20.0;

/// The running screen session's chat, from [`begin_session`] to [`end_session`].
struct Chat {
    administrator: String,
    outgoing: Sender<String>,
    transcript: String,
    /// Built on first use — most sessions never chat at all.
    window: Option<ChatWindow>,
}

struct ChatWindow {
    window: Retained<NSWindow>,
    transcript: Retained<NSTextView>,
    input: Retained<NSTextField>,
    /// Held because a control does not retain its target: dropped, Send and Return would message a
    /// freed object.
    _target: Retained<SendTarget>,
}

thread_local! {
    // Only ever touched from the main thread — see the module comment.
    static CHAT: RefCell<Option<Chat>> = RefCell::new(None);
}

define_class!(
    // SAFETY:
    // - NSObject imposes no subclassing requirements.
    // - This class does not implement Drop.
    // - MainThreadOnly: it is only ever the target of controls, which AppKit messages on the main
    //   thread.
    #[unsafe(super(NSObject))]
    #[thread_kind = MainThreadOnly]
    #[name = "KintsugiChatSendTarget"]
    struct SendTarget;

    unsafe impl NSObjectProtocol for SendTarget {}

    impl SendTarget {
        // The Send button's action, and the text field's, which a text field sends on Return.
        #[unsafe(method(send:))]
        fn send(&self, _sender: Option<&AnyObject>) {
            send_typed_message();
        }
    }
);

impl SendTarget {
    fn new(mtm: MainThreadMarker) -> Retained<Self> {
        let this = mtm.alloc::<Self>().set_ivars(());
        unsafe { msg_send![super(this), init] }
    }
}

/// A screen session has started, with `administrator` at the other end. Opens nothing: the window
/// appears with the administrator's first message, or when the user picks "Chat with Support…".
pub fn begin_session(administrator: String, outgoing: Sender<String>) {
    dispatch::Queue::main().exec_async(move || {
        let chat = Chat { administrator, outgoing, transcript: String::new(), window: None };
        CHAT.with(|cell| close(cell.replace(Some(chat))));
    });
}

/// The session is over, so the window and its transcript go with it. Called from the session
/// thread's cleanup in `remote_control`, beside `tray_menu::report_remote_session(None)`, so every
/// way out of a session reaches it.
pub fn end_session() {
    dispatch::Queue::main().exec_async(|| CHAT.with(|cell| close(cell.take())));
}

/// A message from the administrator. Shows the window — creating it on the first message, and
/// bringing it back if the user closed it — without making it key, so somebody typing into another
/// application is not interrupted mid-word.
pub fn administrator_said(text: String) {
    dispatch::Queue::main().exec_async(move || {
        let Some(mtm) = MainThreadMarker::new() else { return };
        CHAT.with(|cell| {
            let mut borrowed = cell.borrow_mut();
            let Some(chat) = borrowed.as_mut() else { return };

            append_message(&mut chat.transcript, &chat.administrator, &text);
            if let Some(window) = shown(chat, mtm) {
                window.window.orderFrontRegardless();
            }
        });
    });
}

/// The menu bar's "Chat with Support…": the user starting the conversation, so unlike
/// [`administrator_said`] this does take the keyboard, with the cursor in the message field.
pub fn open_from_menu() {
    dispatch::Queue::main().exec_async(|| {
        let Some(mtm) = MainThreadMarker::new() else { return };
        let opened = CHAT.with(|cell| {
            let mut borrowed = cell.borrow_mut();
            let Some(window) = borrowed.as_mut().and_then(|chat| shown(chat, mtm)) else { return false };

            window.window.makeKeyAndOrderFront(None);
            let input: &NSResponder = &window.input;
            window.window.makeFirstResponder(Some(input));
            true
        });

        // An `Accessory` app is not activated by showing a window, so without this the window
        // appears behind the application the user was in, with no keyboard.
        if opened {
            NSApplication::sharedApplication(mtm).activate();
        }
    });
}

/// Sends what is in the message field. Runs on the main thread, as a control's action.
fn send_typed_message() {
    CHAT.with(|cell| {
        let mut borrowed = cell.borrow_mut();
        let Some(chat) = borrowed.as_mut() else { return };
        let Some(window) = chat.window.as_ref() else { return };

        let Some(text) = outgoing_text(&window.input.stringValue().to_string()) else { return };
        window.input.setStringValue(&NSString::from_str(""));

        // Fails only once the session loop has gone, and `end_session` is then already queued.
        let _ = chat.outgoing.send(text.clone());
        append_message(&mut chat.transcript, "You", &text);
        refresh(window, &chat.transcript);
    });
}

/// The window, created if this is its first showing and brought up to date with the transcript.
/// `None` only if AppKit did not hand back the text view it documents, which is logged.
fn shown(chat: &mut Chat, mtm: MainThreadMarker) -> Option<&ChatWindow> {
    if chat.window.is_none() {
        chat.window = create(mtm, &chat.administrator);
    }

    let window = chat.window.as_ref()?;
    refresh(window, &chat.transcript);
    Some(window)
}

fn close(chat: Option<Chat>) {
    if let Some(window) = chat.and_then(|chat| chat.window) {
        window.window.orderOut(None);
    }
}

fn refresh(window: &ChatWindow, transcript: &str) {
    let text = NSString::from_str(transcript);
    window.transcript.setString(&text);
    // To the newest message: a chat is read from the bottom.
    window.transcript.scrollRangeToVisible(NSRange::new(text.length(), 0));
}

fn create(mtm: MainThreadMarker, administrator: &str) -> Option<ChatWindow> {
    // `scrollableTextView` rather than an NSTextView put into an NSScrollView by hand, which takes
    // six more calls to make the text view grow with its content and wrap at the scroll view's width.
    let scroll = NSTextView::scrollableTextView(mtm);
    let Some(transcript) = scroll.documentView().and_then(|view| view.downcast::<NSTextView>().ok()) else {
        logging::error("could not open the support chat window: AppKit's scrollable text view had no text view in it");
        return None;
    };
    transcript.setEditable(false);
    transcript.setFont(Some(&NSFont::systemFontOfSize(NSFont::systemFontSize())));
    scroll.setFrame(NSRect::new(
        NSPoint::new(MARGIN, 52.0),
        NSSize::new(WIDTH - 2.0 * MARGIN, HEIGHT - 52.0 - MARGIN),
    ));

    let target = SendTarget::new(mtm);

    let input = NSTextField::textFieldWithString(&NSString::from_str(""), mtm);
    input.setPlaceholderString(Some(&NSString::from_str("Type a message")));
    input.setFrame(NSRect::new(
        NSPoint::new(MARGIN, 14.0),
        NSSize::new(WIDTH - 3.0 * MARGIN - BUTTON_WIDTH, 24.0),
    ));
    // SAFETY: `send:` is defined on `target`, which `ChatWindow` keeps alive as long as the controls.
    unsafe {
        input.setTarget(Some(&target));
        input.setAction(Some(sel!(send:)));
    }

    // SAFETY: as above.
    let send = unsafe {
        NSButton::buttonWithTitle_target_action(&NSString::from_str("Send"), Some(&target), Some(sel!(send:)), mtm)
    };
    send.setFrame(NSRect::new(NSPoint::new(WIDTH - MARGIN - BUTTON_WIDTH, 10.0), NSSize::new(BUTTON_WIDTH, 32.0)));

    let content_rect = NSRect::new(NSPoint::new(0.0, 0.0), NSSize::new(WIDTH, HEIGHT));
    // SAFETY: as progress_window::create — `mtm.alloc()` proves this runs on the main thread.
    let window = unsafe {
        NSWindow::initWithContentRect_styleMask_backing_defer(
            mtm.alloc(),
            content_rect,
            NSWindowStyleMask::Titled | NSWindowStyleMask::Closable,
            NSBackingStoreType::Buffered,
            false,
        )
    };
    // SAFETY: false is the safe direction. The close button only hides this window — the session,
    // not the user, decides when it goes — and left true, AppKit would release on close a window
    // `CHAT` still holds and releases again later.
    unsafe { window.setReleasedWhenClosed(false) };
    window.setTitle(&NSString::from_str(&format!("Chat with {administrator}")));
    // Above the application being supported, which is where the user's attention will be.
    window.setLevel(NSFloatingWindowLevel);

    let content_view = NSView::initWithFrame(mtm.alloc(), content_rect);
    content_view.addSubview(&scroll);
    content_view.addSubview(&input);
    content_view.addSubview(&send);
    window.setContentView(Some(&content_view));

    // Bottom right of the screen, out of the way of whatever is being fixed in the middle of it.
    match NSScreen::mainScreen(mtm) {
        Some(screen) => {
            let visible = screen.visibleFrame();
            window.setFrameOrigin(NSPoint::new(
                visible.origin.x + visible.size.width - window.frame().size.width - SCREEN_MARGIN,
                visible.origin.y + SCREEN_MARGIN,
            ));
        }
        None => window.center(),
    }

    Some(ChatWindow { window, transcript, input, _target: target })
}

/// Adds one message to the transcript, labelled with who said it.
fn append_message(transcript: &mut String, author: &str, text: &str) {
    if !transcript.is_empty() {
        transcript.push_str("\n\n");
    }
    transcript.push_str(author);
    transcript.push_str(": ");
    transcript.push_str(text.trim());
}

/// What the user typed as it will be sent, or `None` for nothing worth sending. Cut to the
/// protocol's limit here as well as in `ChatMessage::new`, so the transcript shows what the
/// administrator actually received.
fn outgoing_text(typed: &str) -> Option<String> {
    let text = typed.trim();
    (!text.is_empty()).then(|| text.chars().take(MAX_CHAT_CHARS).collect())
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn each_message_is_labelled_and_set_apart_from_the_last() {
        let mut transcript = String::new();
        append_message(&mut transcript, "Alex Admin", "Can you see the printer dialog?\n");
        append_message(&mut transcript, "You", "  Yes  ");

        assert_eq!(transcript, "Alex Admin: Can you see the printer dialog?\n\nYou: Yes");
    }

    #[test]
    fn a_blank_message_is_not_sent() {
        assert_eq!(outgoing_text(""), None);
        assert_eq!(outgoing_text(" \n\t "), None);
        assert_eq!(outgoing_text("  thanks \n"), Some("thanks".to_string()));
    }

    #[test]
    fn a_long_message_is_cut_to_what_the_protocol_carries() {
        let sent = outgoing_text(&"é".repeat(MAX_CHAT_CHARS + 10)).unwrap();
        assert_eq!(sent.chars().count(), MAX_CHAT_CHARS);
    }
}
