//! `kintsugi-agent.exe --preview-popups [--organisation "<name>"] [--logo <file.png>]`
//!
//! Shows every window this agent can put in front of a user, one after another, with example
//! content: the updates prompt, the final notice, the progress window, the remote control consent
//! dialog, and the session banner with the support chat. It exists because none of them can be
//! summoned on demand in the real flow — the prompt needs a due cycle with work pending, the consent
//! dialog an administrator on the other end — and how they look is exactly what the tests cannot
//! check.
//!
//! It touches nothing: no service, no queue, no network, no schedule. It runs on any Windows PC,
//! with or without the agent installed. `--organisation` and `--logo` stand in for the server's
//! patching policy (see `branding::Branding::preview_with`), so a logo can be judged before it is
//! uploaded. Notifications are not shown: they need the notification-area icon, and Windows draws
//! them itself.

use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::Arc;
use std::time::{Duration, Instant};

use anyhow::{Context, Result};

use crate::branding::Branding;
use crate::chat_window::ChatWindow;
use crate::{dialogs, logging, progress_window, session_banner};

const EXAMPLE_APPS: [&str; 3] = ["Google Chrome", "Zoom Workplace", "7-Zip"];
const EXAMPLE_REQUESTER: &str = "someone@example.com";
/// Long enough to read anything; every dialog has a button, so nobody waits this out.
const DIALOG_TIMEOUT_SECONDS: u64 = 10 * 60;

pub fn run(args: &[String]) -> Result<()> {
    let organisation = value_after(args, "--organisation").cloned();
    let logo = match value_after(args, "--logo") {
        Some(path) => Some(std::fs::read(path).with_context(|| format!("could not read the logo at {path}"))?),
        None => None,
    };
    Branding::preview_with(organisation, logo);

    let apps: Vec<String> = EXAMPLE_APPS.iter().map(|name| name.to_string()).collect();

    logging::info("preview: the updates prompt");
    dialogs::confirm_patch("2 hours", 3, &apps, true, DIALOG_TIMEOUT_SECONDS)?;

    logging::info("preview: the final notice, once every postpone is used");
    dialogs::final_notice(5, &apps, DIALOG_TIMEOUT_SECONDS)?;

    logging::info("preview: the progress window");
    let steps = [
        ("Updates start in 5 minutes. Save your work.".to_string(), 0, 0),
        ("Updating Google Chrome to 131.0".to_string(), 0, 4),
        ("Updating Zoom Workplace to 6.2.11".to_string(), 1, 4),
        ("Updating 7-Zip to 24.08".to_string(), 2, 4),
        ("Installing Windows updates. This may take a while.".to_string(), 3, 4),
    ];
    for (current, completed, total) in steps {
        progress_window::show_and_update(&current, completed, total);
        pump_messages_for(Duration::from_secs(3));
    }
    progress_window::hide();

    logging::info("preview: the remote control consent dialog");
    dialogs::confirm_remote_control(
        EXAMPLE_REQUESTER,
        &["While Windows shows a security prompt, only that prompt can be used remotely.".to_string()],
        DIALOG_TIMEOUT_SECONDS,
    )?;

    logging::info("preview: the remote session banner and the support chat (press End session, or wait 60 seconds)");
    let ended = Arc::new(AtomicBool::new(false));
    let chat_requested = Arc::new(AtomicBool::new(false));
    let banner = session_banner::show(EXAMPLE_REQUESTER.to_string(), ended.clone(), chat_requested.clone());
    let chat = ChatWindow::open(EXAMPLE_REQUESTER.to_string());
    chat.receive("Hi, it's the service desk. I can see your screen now.".to_string());
    chat.receive("Could you open the application that keeps closing?\nType here if anything is unclear.".to_string());
    let shown = Instant::now();
    while !ended.load(Ordering::SeqCst) && shown.elapsed() < Duration::from_secs(60) {
        if chat_requested.swap(false, Ordering::SeqCst) {
            chat.show();
        }
        for text in chat.sent() {
            logging::info(&format!("preview: the chat sent {text:?}"));
        }
        std::thread::sleep(Duration::from_millis(100));
    }
    chat.close();
    banner.close();

    logging::info("preview: done");
    Ok(())
}

/// The value following `flag` in `args`, if both are there.
fn value_after<'a>(args: &'a [String], flag: &str) -> Option<&'a String> {
    args.iter().position(|arg| arg == flag).and_then(|index| args.get(index + 1))
}

/// Keeps this thread's windows responsive for `duration` — the progress window is modeless, and
/// in the real agent the notification-area icon's message loop is what drives it.
fn pump_messages_for(duration: Duration) {
    use windows_sys::Win32::UI::WindowsAndMessaging::{DispatchMessageW, PeekMessageW, TranslateMessage, MSG, PM_REMOVE};

    let until = Instant::now() + duration;
    while Instant::now() < until {
        // SAFETY: a message loop over this thread's own windows, which is what PeekMessageW with a
        // null window handle serves.
        unsafe {
            let mut message: MSG = std::mem::zeroed();
            while PeekMessageW(&mut message, std::ptr::null_mut(), 0, 0, PM_REMOVE) != 0 {
                TranslateMessage(&message);
                DispatchMessageW(&message);
            }
        }
        std::thread::sleep(Duration::from_millis(15));
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn args(values: &[&str]) -> Vec<String> {
        values.iter().map(|value| value.to_string()).collect()
    }

    #[test]
    fn value_after_reads_the_argument_following_its_flag() {
        let given = args(&["kintsugi-agent.exe", "--preview-popups", "--organisation", "Example IT", "--logo", "C:\\logo.png"]);

        assert_eq!(value_after(&given, "--organisation").map(String::as_str), Some("Example IT"));
        assert_eq!(value_after(&given, "--logo").map(String::as_str), Some("C:\\logo.png"));
    }

    #[test]
    fn value_after_is_none_for_a_missing_flag_or_a_flag_with_nothing_after_it() {
        assert_eq!(value_after(&args(&["--preview-popups"]), "--logo"), None);
        assert_eq!(value_after(&args(&["--preview-popups", "--logo"]), "--logo"), None);
    }
}
