use std::cell::RefCell;

use crate::branding::Branding;
use crate::popup::{Button, ButtonStyle, Card, Modeless, Progress};

thread_local! {
    // Only ever touched from the UI thread — created lazily on first use rather than up front, so a
    // PC that never actually patches never has a window sitting around at all. A window belongs to
    // the thread that created it, and only that thread may touch it, so a thread-local expresses
    // exactly the right scope; the same reasoning the macOS agent applies to its own non-Send
    // AppKit objects.
    static WINDOW: RefCell<Option<Modeless>> = const { RefCell::new(None) };
}

/// Shows the progress window (creating it on first use) with `current` as its status line and a
/// bar reflecting `completed`/`total` — empty while `total` is zero, which is the five-minute
/// warning before anything has started. Also brings it to the foreground, since a process with no
/// visible main window doesn't otherwise get focus when a window opens.
///
/// Must be called on the UI thread — see `tray_menu::apply_status`, the only caller.
pub fn show_and_update(current: &str, completed: usize, total: usize) {
    let card = progress_card(current, completed, total);
    WINDOW.with(|cell| {
        let mut window = cell.borrow_mut();
        // SAFETY: this is the notification-area thread, which pumps messages for the life of the
        // process, and the only thread that ever touches this window.
        unsafe {
            match window.as_ref() {
                Some(existing) => existing.update(card),
                None => match Modeless::create(card, &Branding::current()) {
                    Some(created) => *window = Some(created),
                    None => {
                        crate::logging::warn("could not create the progress window; patching will continue without it");
                        return;
                    }
                },
            }
            if let Some(window) = window.as_ref() {
                window.show();
            }
        }
    });
}

/// Hides the progress window — called once a patch cycle finishes (success, failure, or a delay)
/// and the agent goes back to idle. A no-op if the window was never created.
///
/// Hidden rather than destroyed: a patch cycle recurs, and keeping the window means the next one
/// doesn't have to rebuild it (and can't fail to). The organisation's logo is read when the window
/// is first made, so a logo changed mid-session shows from the next sign-in.
pub fn hide() {
    WINDOW.with(|cell| {
        if let Some(window) = cell.borrow().as_ref() {
            // SAFETY: on the thread that created it, as above.
            unsafe { window.hide() };
        }
    });
}

/// What the window says. Pure, so the wording is testable without a window.
///
/// The close box and "Hide" both only hide it: there is nothing to cancel — the cycle carries on
/// either way — and the window comes back at the next step.
fn progress_card(current: &str, completed: usize, total: usize) -> Card {
    let started = total > 0;
    Card {
        eyebrow: "Software updates".to_string(),
        title: if started { "Installing updates" } else { "Updates start soon" }.to_string(),
        body: None,
        rows: Vec::new(),
        callout: None,
        progress: Some(Progress { label: current.to_string(), completed, total }),
        note: None,
        footer_note: Some(
            if started { "You can keep working. Don't turn off your computer." } else { "Save your work before they start." }
                .to_string(),
        ),
        buttons: vec![Button { label: "Hide".to_string(), style: ButtonStyle::Neutral }],
        focused: 0,
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn the_warning_before_anything_starts_is_not_called_installing() {
        let card = progress_card("Updates start in 5 minutes", 0, 0);

        assert_eq!(card.title, "Updates start soon");
    }

    #[test]
    fn once_installing_it_says_so_and_tells_the_user_they_can_keep_working() {
        let card = progress_card("Updating Zoom Workplace", 1, 3);

        assert_eq!(card.title, "Installing updates");
        assert!(card.footer_note.unwrap().contains("keep working"));
        assert_eq!(card.progress.unwrap().completed, 1);
    }
}
