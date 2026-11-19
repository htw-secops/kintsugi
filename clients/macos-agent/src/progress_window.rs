//! The window that stays up while a patch cycle runs: the same card as every other window this agent
//! draws (see `popup`), with a progress bar and a Hide button, updated with each step. The Windows
//! agent's `progress_window.rs` is the same card; keep the wording in step.

use std::cell::RefCell;

use crate::popup::{Button, ButtonStyle, Card, Modeless, Progress};

thread_local! {
    // Only ever touched from the main thread, where `tray_menu::report_status` runs. Opened on
    // first use rather than up front, so a Mac that never patches never has a window at all.
    static WINDOW: RefCell<Option<Modeless>> = const { RefCell::new(None) };
}

/// Shows the progress window — opening it on first use, and bringing it back if Hide put it away —
/// with `current` as its status line and a bar for `completed` of `total` (empty while `total` is
/// zero, during the warning before anything starts).
pub fn show_and_update(current: &str, completed: usize, total: usize) {
    let card = progress_card(current, completed, total);
    WINDOW.with(|cell| {
        let mut window = cell.borrow_mut();
        // An update that fails means the script has gone (somebody quit osascript, say), so a new
        // window takes its place rather than the cycle running on with none.
        if let Some(existing) = window.as_mut() {
            if existing.update(&card).is_ok() {
                return;
            }
        }
        *window = match Modeless::open(&card) {
            Ok(opened) => Some(opened),
            Err(err) => {
                crate::logging::warn(&format!("could not open the progress window; patching continues without it: {err:#}"));
                None
            }
        };
    });
}

/// Closes the progress window — once a cycle finishes, fails or is delayed and the agent goes back
/// to idle. A no-op if it was never opened.
pub fn hide() {
    WINDOW.with(|cell| drop(cell.borrow_mut().take()));
}

fn progress_card(current: &str, completed: usize, total: usize) -> Card {
    let started = total > 0;
    Card {
        eyebrow: "Software updates".to_string(),
        title: if started { "Installing updates" } else { "Updates start soon" }.to_string(),
        body: None,
        rows: Vec::new(),
        more: 0,
        callout: None,
        progress: Some(Progress { label: current.to_string(), completed, total }),
        field: None,
        note: None,
        footer_note: Some(
            if started { "You can keep working. Don't turn off your Mac." } else { "Save your work before they start." }.to_string(),
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
        let card = progress_card("Updates start in 5 minutes. Save your work.", 0, 0);

        assert_eq!(card.title, "Updates start soon");
    }

    #[test]
    fn once_installing_it_says_so_and_tells_the_user_they_can_keep_working() {
        let card = progress_card("gcloud-cli \u{2192} 551.0.0", 1, 3);

        assert_eq!(card.title, "Installing updates");
        assert!(card.footer_note.unwrap().contains("keep working"));
        assert_eq!(card.progress.unwrap().completed, 1);
    }
}
