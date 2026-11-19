//! What each question to the person at the keyboard says, and what their answer means. How it
//! looks is `popup`'s; this file only builds the [`Card`]s, which keeps every word testable without
//! putting a window on screen.

use anyhow::Result;

use crate::branding::Branding;
use crate::popup::{self, Badge, Button, ButtonStyle, Callout, Card, Row, Tone};

#[derive(Debug, PartialEq, Eq)]
pub enum ConfirmChoice {
    PatchNow,
    Delay,
    /// The dialog stood for a whole delay period of wall-clock time without an answer (see
    /// `popup::DEADLINE_POLL_MS`), or was closed with its close box.
    ///
    /// It counts against the delay budget — the user was asked and said nothing — but **not** the
    /// way an explicit `Delay` does: the dialog stands there for a whole delay period, so that
    /// period is already spent by the time it gives up, and postponing by another one on top would
    /// count the budget down half as fast as the policy says. See
    /// `ScheduleState::register_unanswered_prompt`.
    TimedOut,
}

/// What the person at the keyboard said when asked to hand over control of this host.
///
/// **A separate type from [`ConfirmChoice`] on purpose, because a timeout means the opposite
/// thing.** There, nobody answering means "they were not at the desk, so count it as a delay and
/// patch later" — the user never refused, and patching happens regardless. Here, nobody answering
/// means **nobody consented**, and the only safe reading of silence is refusal. Reusing that enum
/// would have left the safe default one careless `match` arm away from handing an unattended
/// desktop to whoever asked. Kept identical to the macOS agent's `RemoteControlChoice`.
#[derive(Debug, PartialEq, Eq)]
pub enum RemoteControlChoice {
    Allow,
    Deny,
    /// The dialog was left up until its own timer dismissed it. Treated exactly as [`Self::Deny`]
    /// by every caller, and kept distinct only so the audit record can tell an empty desk from a
    /// deliberate refusal.
    TimedOut,
}

const UPDATES: &str = "Software updates";

/// How many application names the dialog lists before summarising the rest. A host that has been
/// offline for a while can legitimately have dozens of pending updates, and an unbounded list would
/// grow the dialog off the bottom of the screen. Kept identical in the macOS and Linux agents.
const MAX_LISTED_APPS: usize = 10;

/// Asks the host user to hand over control, and returns what they said.
///
/// **Deny has the keyboard focus**, so Return or Space on a prompt that appeared unannounced
/// refuses, and it sits on the left as the safe choice. Allow is an outline button rather than a
/// filled one: the filled button is the one a hand moves to without reading, and here that must not
/// be the one that hands over somebody's desktop.
pub fn confirm_remote_control(requested_by: &str, restrictions: &[String], timeout_seconds: u64) -> Result<RemoteControlChoice> {
    crate::logging::info(&format!("asking the console user to approve remote control for {requested_by}"));

    let card = remote_control_card(requested_by, restrictions, timeout_seconds);
    let choice = match popup::run_modal(card, timeout_seconds, &Branding::current())? {
        Some(1) => RemoteControlChoice::Allow,
        Some(_) => RemoteControlChoice::Deny,
        None => RemoteControlChoice::TimedOut,
    };

    crate::logging::info(&format!(
        "console user chose: {}",
        match choice {
            RemoteControlChoice::Allow => "allow remote control",
            RemoteControlChoice::Deny => "deny remote control",
            RemoteControlChoice::TimedOut => "timed out (treated as a refusal)",
        }
    ));

    Ok(choice)
}

/// The consent dialog. It names the administrator, says plainly what is being granted, and says
/// how to end it. All three matter: a dialog reading "allow remote access?" with no name is one
/// people click through, and one that does not mention the way out leaves someone who regrets it
/// with no visible exit.
fn remote_control_card(requested_by: &str, restrictions: &[String], timeout_seconds: u64) -> Card {
    let closes = format!("This request closes in {} and is declined if you don't answer.", duration(timeout_seconds));
    let note = std::iter::once(closes).chain(restrictions.iter().cloned()).collect::<Vec<_>>().join(" ");
    Card {
        eyebrow: "Remote support".to_string(),
        title: "Allow remote control?".to_string(),
        body: Some(format!("{requested_by} is asking to see your screen and use your keyboard and mouse.")),
        rows: Vec::new(),
        more: 0,
        callout: Some(Callout {
            tone: Tone::Info,
            title: None,
            text: "A bar at the top of your screen shows who is connected for the whole session. Press End session on it \
                   at any time."
                .to_string(),
        }),
        progress: None,
        note: Some(note),
        footer_note: None,
        buttons: vec![
            Button { label: "Deny".to_string(), style: ButtonStyle::Neutral },
            Button { label: "Allow".to_string(), style: ButtonStyle::Outline },
        ],
        focused: 0,
    }
}

/// Shows the install-or-postpone dialog. Only called when there is at least one postpone left —
/// `final_notice` covers the case with none — and only once the caller has confirmed there is real
/// work, so the dialog names it rather than saying a generic "patches are ready".
///
/// `timeout_seconds` bounds how long it stays up, on the wall clock, before it is taken down
/// unanswered (`ConfirmChoice::TimedOut`). Callers pass the delay period itself, so an ignored
/// dialog has spent one whole delay period by the time it gives up — which is why that outcome
/// counts the budget down without postponing the cycle any further.
pub fn confirm_patch(
    delay_label: &str,
    delays_remaining: u32,
    app_names: &[String],
    os_update_available: bool,
    timeout_seconds: u64,
) -> Result<ConfirmChoice> {
    crate::logging::info(&format!("showing patch confirmation dialog ({delays_remaining} delay(s) available)"));

    let card = confirm_card(delay_label, delays_remaining, app_names, os_update_available, crate::os_update::reboot_pending());
    let choice = match popup::run_modal(card, timeout_seconds, &Branding::current())? {
        Some(1) => ConfirmChoice::PatchNow,
        Some(_) => ConfirmChoice::Delay,
        None => ConfirmChoice::TimedOut,
    };

    crate::logging::info(&format!(
        "user chose: {}",
        match choice {
            ConfirmChoice::Delay => "delay",
            ConfirmChoice::PatchNow => "patch now",
            ConfirmChoice::TimedOut => "timed out (counts as a delay, and is asked again at once)",
        }
    ));

    Ok(choice)
}

/// The install-or-postpone dialog.
///
/// The applications are listed rather than only counted: "3 application updates are ready"
/// doesn't tell someone deciding whether to postpone whether the thing they have open right now is
/// about to close. "Install now" has the keyboard focus, as "Patch Now" did and as the macOS
/// agent's dialog has it: pressing Return should proceed, not postpone.
fn confirm_card(delay_label: &str, delays_remaining: u32, app_names: &[String], os_update_available: bool, restart_pending: bool) -> Card {
    let mut rows: Vec<Row> = app_names.iter().take(MAX_LISTED_APPS).map(|name| Row { label: name.clone(), badge: None }).collect();
    if os_update_available {
        rows.push(Row {
            label: "Windows update".to_string(),
            badge: Some(Badge { text: "May restart".to_string(), tone: Tone::Warning }),
        });
    }

    // A restart that is already pending is a fact; before an install a new one is only a
    // possibility, so the other wordings stay hedged.
    let body = if restart_pending {
        "This computer is already waiting for a restart, and installing may close apps. Save your work first."
    } else if app_names.is_empty() {
        "Windows will install an update and may need to restart. Save your work first."
    } else {
        "These apps will close while they update. Save your work first."
    };

    Card {
        eyebrow: UPDATES.to_string(),
        title: "Updates are ready to install".to_string(),
        body: Some(body.to_string()),
        rows,
        // Drawn as "… and N more"; `popup` folds further names into it on a screen too short for ten.
        more: app_names.len().saturating_sub(MAX_LISTED_APPS),
        callout: None,
        progress: None,
        note: Some(format!(
            "You can postpone {} more {}. If nobody answers within {delay_label}, that counts as one.",
            delays_remaining,
            if delays_remaining == 1 { "time" } else { "times" }
        )),
        footer_note: None,
        buttons: vec![
            Button { label: format!("Remind me in {delay_label}"), style: ButtonStyle::Neutral },
            Button { label: "Install now".to_string(), style: ButtonStyle::Primary },
        ],
        focused: 1,
    }
}

/// The notice shown when every postpone has been used: patching starts at the end of the warning
/// period whatever happens, and this says so.
///
/// It *is* the warning rather than a preamble to it — it stands for as much of the period as the
/// user leaves it up (`timeout_seconds` is the whole period), and the caller waits out the rest.
pub fn final_notice(minutes: u64, app_names: &[String], timeout_seconds: u64) -> Result<()> {
    crate::logging::info(&format!("showing the final notice: updates install in {minutes} minute(s)"));
    popup::run_modal(final_notice_card(minutes, app_names), timeout_seconds, &Branding::current()).map(|_| ())
}

fn final_notice_card(minutes: u64, app_names: &[String]) -> Card {
    let closing = match name_the_apps(app_names) {
        Some(names) => format!("{names} will close."),
        None => "Some apps may close.".to_string(),
    };
    Card {
        eyebrow: UPDATES.to_string(),
        title: format!("Updates will install in {minutes} minute{}", if minutes == 1 { "" } else { "s" }),
        body: None,
        rows: Vec::new(),
        more: 0,
        callout: Some(Callout {
            tone: Tone::Warning,
            title: Some("Save your work now".to_string()),
            text: format!("{closing} These updates have been postponed as many times as allowed."),
        }),
        progress: None,
        note: None,
        footer_note: None,
        buttons: vec![Button { label: "Got it".to_string(), style: ButtonStyle::Primary }],
        focused: 0,
    }
}

/// "Google Chrome, Zoom and 7-Zip", or the first two and a count past three. `None` for no names.
pub fn name_the_apps(app_names: &[String]) -> Option<String> {
    match app_names {
        [] => None,
        [only] => Some(only.clone()),
        [first, second] => Some(format!("{first} and {second}")),
        [first, second, third] => Some(format!("{first}, {second} and {third}")),
        [first, second, rest @ ..] => Some(format!("{first}, {second} and {} other apps", rest.len())),
    }
}

/// "1 minute", "90 seconds" — whole minutes when it divides, which is what a person would say.
fn duration(seconds: u64) -> String {
    if seconds >= 60 && seconds % 60 == 0 {
        let minutes = seconds / 60;
        format!("{minutes} minute{}", if minutes == 1 { "" } else { "s" })
    } else {
        format!("{seconds} second{}", if seconds == 1 { "" } else { "s" })
    }
}

/// Best-effort — a failed notification shouldn't ever be treated as a reason to abort patching.
///
/// Delivered as a balloon on this agent's own notification-area icon (see `tray_menu::notify`),
/// which is both the platform's equivalent of macOS's `display notification` and the only route
/// available: a toast raised any other way needs a registered AppUserModelID and a Start Menu
/// shortcut to appear at all. `title` is the bold line; keep it to a few words that say what
/// happened.
pub fn notify(title: &str, message: &str) {
    crate::logging::info(&format!("notification: {title} — {message}"));
    crate::tray_menu::notify(title, message);
}

#[cfg(test)]
mod tests {
    use super::*;

    fn names(names: &[&str]) -> Vec<String> {
        names.iter().map(|name| name.to_string()).collect()
    }

    #[test]
    fn the_consent_dialog_names_who_is_asking_and_how_to_stop_it() {
        let card = remote_control_card("admin@example.com", &[], 60);

        assert!(card.body.as_deref().unwrap().contains("admin@example.com is asking"), "{card:?}");
        assert!(card.body.as_deref().unwrap().contains("keyboard and mouse"), "{card:?}");
        // Somebody who regrets allowing it needs to know there is a way out, and it has to name the
        // thing that actually exists: the session helper's banner.
        let callout = card.callout.unwrap().text;
        assert!(callout.contains("End session") && callout.contains("top of your screen"), "{callout}");
    }

    #[test]
    fn the_consent_dialog_says_silence_is_a_refusal() {
        let card = remote_control_card("admin@example.com", &[], 60);

        assert!(card.note.unwrap().contains("closes in 1 minute and is declined"));
    }

    #[test]
    fn the_consent_dialog_lists_what_the_session_cannot_do() {
        let card = remote_control_card("admin@example.com", &["Elevated windows cannot be clicked.".to_string()], 60);

        assert!(card.note.unwrap().contains("Elevated windows cannot be clicked."));
    }

    /// Return on a prompt that appeared unannounced must refuse, and the filled button — the one a
    /// hand moves to without reading — must not be the one that hands over a desktop.
    #[test]
    fn a_pending_restart_is_stated_as_fact_and_otherwise_stays_hedged() {
        let pending = confirm_card("2 hours", 3, &[], true, true).body.unwrap();
        assert!(pending.contains("already waiting for a restart"), "{pending}");

        let not_pending = confirm_card("2 hours", 3, &[], true, false).body.unwrap();
        assert!(not_pending.contains("may need to restart"), "{not_pending}");
    }

    #[test]
    fn deny_has_the_focus_and_allow_is_never_the_filled_button() {
        let card = remote_control_card("admin@example.com", &[], 60);

        assert_eq!(card.buttons[card.focused].label, "Deny");
        assert!(card.buttons.iter().all(|button| button.style != ButtonStyle::Primary));
        // `confirm_remote_control` reads index 1 as Allow.
        assert_eq!(card.buttons[1].label, "Allow");
    }

    #[test]
    fn install_now_is_the_focused_primary_on_the_right() {
        let card = confirm_card("2 hours", 3, &names(&["Firefox"]), false, false);

        // `confirm_patch` reads index 1 as PatchNow.
        assert_eq!(card.buttons[1].label, "Install now");
        assert_eq!(card.buttons[1].style, ButtonStyle::Primary);
        assert_eq!(card.focused, 1);
        assert_eq!(card.buttons[0].label, "Remind me in 2 hours");
    }

    #[test]
    fn the_postpones_left_read_as_a_person_would_say_them() {
        assert!(confirm_card("2 hours", 3, &names(&["Firefox"]), false, false).note.unwrap().contains("postpone 3 more times"));
        assert!(confirm_card("1 day", 1, &names(&["Firefox"]), false, false).note.unwrap().contains("postpone 1 more time."));
    }

    #[test]
    fn the_confirm_dialog_lists_the_applications_that_will_close() {
        let card = confirm_card("2 hours", 3, &names(&["Firefox", "Slack"]), false, false);

        assert_eq!(card.rows.iter().map(|row| row.label.as_str()).collect::<Vec<_>>(), vec!["Firefox", "Slack"]);
        assert!(card.body.unwrap().contains("will close"));
    }

    /// A host that has been offline for a while can have dozens pending; the dialog has to stay on
    /// screen, so past the cap the rest are counted rather than named.
    #[test]
    fn the_confirm_dialog_summarises_the_tail_of_a_long_list() {
        let all: Vec<String> = (1..=14).map(|n| format!("App {n}")).collect();

        let card = confirm_card("2 hours", 3, &all, false, false);

        // `popup` draws `more` as the "… and 4 more" line; see its `display_rows` test.
        assert_eq!(card.rows.len(), 10);
        assert_eq!(card.rows[9].label, "App 10");
        assert_eq!(card.more, 4);
    }

    #[test]
    fn a_windows_update_is_its_own_row_and_says_it_may_restart() {
        let card = confirm_card("2 hours", 3, &names(&["Firefox"]), true, false);

        let windows = card.rows.last().unwrap();
        assert_eq!(windows.label, "Windows update");
        assert_eq!(windows.badge.as_ref().unwrap().tone, Tone::Warning);
    }

    #[test]
    fn a_windows_update_alone_says_so_rather_than_naming_apps() {
        let card = confirm_card("2 hours", 3, &[], true, false);

        assert!(card.body.unwrap().starts_with("Windows will install an update"));
        assert_eq!(card.rows.len(), 1);
    }

    #[test]
    fn the_final_notice_names_what_will_close_and_has_one_button() {
        let card = final_notice_card(5, &names(&["Google Chrome", "Zoom", "7-Zip"]));

        assert_eq!(card.title, "Updates will install in 5 minutes");
        assert!(card.callout.unwrap().text.starts_with("Google Chrome, Zoom and 7-Zip will close."));
        assert_eq!(card.buttons.len(), 1);
    }

    #[test]
    fn the_final_notice_reads_correctly_for_one_minute_and_no_names() {
        let card = final_notice_card(1, &[]);

        assert_eq!(card.title, "Updates will install in 1 minute");
        assert!(card.callout.unwrap().text.starts_with("Some apps may close."));
    }

    #[test]
    fn name_the_apps_counts_past_three() {
        assert_eq!(name_the_apps(&names(&["A"])).as_deref(), Some("A"));
        assert_eq!(name_the_apps(&names(&["A", "B"])).as_deref(), Some("A and B"));
        assert_eq!(name_the_apps(&names(&["A", "B", "C", "D", "E"])).as_deref(), Some("A, B and 3 other apps"));
        assert_eq!(name_the_apps(&[]), None);
    }

    #[test]
    fn duration_says_minutes_when_it_can() {
        assert_eq!(duration(60), "1 minute");
        assert_eq!(duration(120), "2 minutes");
        assert_eq!(duration(90), "90 seconds");
        assert_eq!(duration(1), "1 second");
    }
}
