//! Every question this agent asks the person at the Mac, and every notification it shows. Each
//! question is a [`Card`] built here and drawn by `popup` — the wording is the part that has to stay
//! true to what `patch_cycle` found, so it is built by plain functions the tests read directly.
//!
//! The words follow the Windows agent's `dialogs.rs` wherever the two platforms do the same thing;
//! where they differ it is because macOS does — the macOS update restarts this Mac by itself (`-R`,
//! see `os_update::install`) and needs the owner's password, which Windows asks for neither.

use std::process::Command;

use anyhow::{Context, Result};

use crate::popup::{self, Badge, Button, ButtonStyle, Callout, Card, Field, Row, Tone};

#[derive(Debug, PartialEq, Eq)]
pub enum ConfirmChoice {
    PatchNow,
    Delay,
    /// The prompt stood for a whole delay period of wall-clock time without an answer, and was
    /// taken down (see `popup::wait_until`).
    ///
    /// It counts against the delay budget — the user was asked and said nothing — but **not** the
    /// way an explicit `Delay` does: the prompt stands there for a whole delay period, so that
    /// period is already spent by the time it gives up, and postponing by another one on top would
    /// count the budget down half as fast as the policy says. See
    /// `ScheduleState::register_unanswered_prompt`.
    TimedOut,
}

/// What the person at the keyboard said when asked to hand over control of their Mac.
///
/// **A separate type from [`ConfirmChoice`] on purpose, because the polarity of a timeout is the
/// opposite.** There, nobody answering means "they were not at the desk, so count it as a delay and
/// patch later" — the user never said no, and patching is going to happen regardless. Here, nobody
/// answering means **nobody consented**, and the only safe reading of silence is refusal. Reusing
/// that enum would have made the safe default one careless `match` arm away from letting an
/// unattended Mac be taken over.
#[derive(Debug, PartialEq, Eq)]
pub enum RemoteControlChoice {
    Allow,
    Deny,
    /// Nobody answered before the prompt was taken down. Treated exactly as [`Self::Deny`] by every
    /// caller, and kept distinct only so the audit record can tell an empty desk from a deliberate
    /// refusal.
    TimedOut,
}

/// The small capitals over every patching card.
const UPDATES: &str = "Software updates";

/// How many application names the prompt lists before summarising the rest. A host that has been
/// offline for a while can legitimately have dozens of pending updates, and an unbounded list
/// would grow the window off the bottom of the screen. Kept identical in the Windows and Linux
/// agents' dialogs.
const MAX_LISTED_APPS: usize = 10;

/// Asks whether to install now or later, and returns the answer. When no delays are left the
/// caller shows [`final_notice`] instead, since there is nothing left to choose between. Only ever
/// called once the caller has confirmed there is real work — `app_names` and the macOS update
/// describe what that is, so the prompt says something concrete rather than "patches are ready".
///
/// `timeout_seconds` bounds how long the prompt stays up, on the wall clock, before it is taken
/// down unanswered (`ConfirmChoice::TimedOut`). Callers pass the delay period itself, so an ignored
/// prompt has spent one whole delay period by the time it gives up — which is why that outcome
/// counts the budget down without postponing the cycle any further.
pub fn confirm_patch(
    delay_label: &str,
    delays_remaining: u32,
    app_names: &[String],
    os_update_available: bool,
    os_update_version: Option<&str>,
    timeout_seconds: u64,
) -> Result<ConfirmChoice> {
    crate::logging::info(&format!("showing patch confirmation dialog ({delays_remaining} delay(s) available)"));

    let card = confirm_card(delay_label, delays_remaining, app_names, os_update_available.then_some(os_update_version));
    let choice = match popup::choose(&card, timeout_seconds)? {
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

/// The confirm-or-delay card. `os_update` is `Some(version)` when the macOS update is part of the
/// plan, carrying the version when `softwareupdate` gave one.
///
/// The applications are listed rather than only counted: "3 updates are ready" doesn't tell
/// somebody deciding whether to delay whether the thing they have open right now is about to be
/// restarted. The macOS update is named by version, because `-i -a` installs everything applicable
/// and "a macOS update" can mean a 2.9GB point release or an 11.7GB new major version. And the
/// restart is stated as a certainty, only when that update is in the plan: `os_update::install`
/// passes `-R`, so this Mac restarts by itself to finish, and "could require a reboot" would
/// understate what Install now starts.
fn confirm_card(delay_label: &str, delays_remaining: u32, app_names: &[String], os_update: Option<Option<&str>>) -> Card {
    let mut rows: Vec<Row> = app_names.iter().take(MAX_LISTED_APPS).map(|name| Row { label: name.clone(), badge: None }).collect();
    if let Some(version) = os_update {
        let label = version.map_or_else(|| "macOS update".to_string(), |version| format!("macOS {version}"));
        rows.push(Row { label, badge: Some(Badge { text: "Restarts your Mac".to_string(), tone: Tone::Warning }) });
    }

    let body = match (app_names.is_empty(), os_update.is_some()) {
        (false, false) => "These apps will close while they update. Save your work first.",
        (false, true) => {
            "These apps will close while they update, and your Mac will restart to finish the macOS update. Save your \
             work first."
        }
        (true, _) => "Your Mac will restart to finish installing the update. Save your work first.",
    };

    Card {
        eyebrow: UPDATES.to_string(),
        title: "Updates are ready to install".to_string(),
        body: Some(body.to_string()),
        rows,
        more: app_names.len().saturating_sub(MAX_LISTED_APPS),
        callout: None,
        progress: None,
        field: None,
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

/// The notice before an automatic start, once every postponement has been used. Returns when it is
/// dismissed or when `timeout_seconds` have passed, and the two are deliberately indistinguishable
/// here: patching starts either way, and how long the notice stood is what
/// `patch_cycle::acknowledged_by_a_person` reads to decide whether somebody saw it.
pub fn final_notice(minutes: u64, app_names: &[String], os_update_available: bool, timeout_seconds: u64) -> Result<()> {
    crate::logging::info(&format!("showing the final notice: updates install in {minutes} minute(s)"));
    popup::choose(&final_notice_card(minutes, app_names, os_update_available), timeout_seconds).map(|_| ())
}

fn final_notice_card(minutes: u64, app_names: &[String], os_update_available: bool) -> Card {
    let consequence = match (name_the_apps(app_names), os_update_available) {
        (Some(names), true) => format!("{names} will close, and your Mac will restart to finish the macOS update."),
        (Some(names), false) => format!("{names} will close."),
        (None, true) => "Your Mac will restart to finish the macOS update.".to_string(),
        (None, false) => "Some apps may close.".to_string(),
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
            text: format!("{consequence} These updates have been postponed as many times as allowed."),
        }),
        progress: None,
        field: None,
        note: None,
        footer_note: None,
        buttons: vec![Button { label: "Got it".to_string(), style: ButtonStyle::Primary }],
        focused: 0,
    }
}

/// Said when the macOS update installed but `-R` did not restart the Mac — the leftover case
/// `patch_cycle` describes. Returns once dismissed or once `timeout_seconds` have passed.
pub fn restart_notice(timeout_seconds: u64) -> Result<()> {
    crate::logging::info("showing the restart-to-finish notice");
    popup::choose(&restart_notice_card(), timeout_seconds).map(|_| ())
}

fn restart_notice_card() -> Card {
    Card {
        eyebrow: UPDATES.to_string(),
        title: "Restart to finish updating".to_string(),
        body: Some(
            "The macOS update is installed, but your Mac did not restart by itself. Restart it when convenient to finish."
                .to_string(),
        ),
        rows: Vec::new(),
        more: 0,
        callout: None,
        progress: None,
        field: None,
        note: None,
        footer_note: None,
        buttons: vec![Button { label: "Got it".to_string(), style: ButtonStyle::Primary }],
        focused: 0,
    }
}

/// "Firefox", "Firefox and Slack", "Firefox, Slack and Zoom", "Firefox, Slack and 4 other apps" —
/// the apps a sentence can name before it stops being one. As the Windows agent's.
pub fn name_the_apps(app_names: &[String]) -> Option<String> {
    match app_names {
        [] => None,
        [only] => Some(only.clone()),
        [first, second] => Some(format!("{first} and {second}")),
        [first, second, third] => Some(format!("{first}, {second} and {third}")),
        [first, second, rest @ ..] => Some(format!("{first}, {second} and {} other apps", rest.len())),
    }
}

/// Asks the host user to hand over control, and returns what they said.
///
/// `restrictions` is anything the session will not be able to do (see
/// `remote_control::describe_restrictions`), listed in the prompt so the decision is an informed
/// one. It names the administrator, says what is being granted in plain terms, and says how to end
/// it: a prompt reading "allow remote access?" with no name is one people click through, and one
/// that does not mention the menu bar leaves somebody who regrets it with no visible way out.
///
/// Return presses **Deny**, and Allow is never the filled button: pressing Return at a prompt that
/// appeared unannounced must refuse, and silence is a refusal too (`RemoteControlChoice::TimedOut`).
pub fn confirm_remote_control(requested_by: &str, restrictions: &[String], timeout_seconds: u64) -> Result<RemoteControlChoice> {
    crate::logging::info(&format!("asking the console user to approve remote control for {requested_by}"));

    let choice = match popup::choose(&remote_control_card(requested_by, restrictions, timeout_seconds), timeout_seconds)? {
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
            text: "While they are connected, the Kintsugi icon in the menu bar shows who it is. Choose End Remote Session \
                   from it to stop at any time."
                .to_string(),
        }),
        progress: None,
        field: None,
        note: Some(note),
        footer_note: None,
        buttons: vec![
            Button { label: "Deny".to_string(), style: ButtonStyle::Neutral },
            Button { label: "Allow".to_string(), style: ButtonStyle::Outline },
        ],
        focused: 0,
    }
}

/// "1 minute", "90 seconds".
fn duration(seconds: u64) -> String {
    if seconds >= 60 && seconds % 60 == 0 {
        let minutes = seconds / 60;
        format!("{minutes} minute{}", if minutes == 1 { "" } else { "s" })
    } else {
        format!("{seconds} second{}", if seconds == 1 { "" } else { "s" })
    }
}

/// Asks the console user to authorize the macOS install, and returns their password.
///
/// It returns only when it has one. The card has one button and no deadline: Escape does nothing
/// to it, an empty box is not an answer, and the only other way out is an `Err` — osascript could
/// not be run, or there was no window server to draw on. It used to offer Cancel and a ten-minute
/// timeout, and both read as "skip the macOS update this time", so a person who walked away at the
/// wrong moment, or reached for Cancel out of habit, left a Mac that had already downloaded
/// everything unpatched until the next cycle asked again. The person clicked Install now (or spent
/// their delay budget with somebody at the desk — see `patch_cycle::os_update_eligibility`), the
/// applications are already patched and the macOS update is downloaded: leaving the prompt up until
/// it is answered is what makes the cycle finish. Like every card it stays in front and on every
/// space (see `popup.js`), so it is not buried the first time another window takes focus.
///
/// The password goes straight into an `os_update::InstallAuth`; it is never logged.
pub fn request_install_password(username: &str, version: Option<&str>) -> Result<String> {
    crate::logging::info(&format!("asking {username} to authorize the macOS install"));
    let password = popup::ask_password(&install_password_card(username, version))?;
    crate::logging::info(&format!("{username} authorized the macOS install"));
    Ok(password)
}

/// The authorization card. It names the version, for the reason `confirm_card` does, and it says
/// the restart is minutes away rather than some time later: the prompt only appears once the
/// daemon's pre-fetch has staged the update (`main::prefetch_os_updates`), which is what makes that
/// true, and `-R` restarts the Mac without necessarily closing anything first. It offers no way out,
/// because there is none — saying so is kinder than leaving somebody to look for Cancel.
fn install_password_card(username: &str, version: Option<&str>) -> Card {
    let (title, update) = match version {
        Some(version) => (format!("Ready to install macOS {version}"), "The update"),
        None => ("Ready to install the macOS update".to_string(), "The macOS update"),
    };
    Card {
        eyebrow: UPDATES.to_string(),
        title,
        body: Some(format!(
            "{update} has downloaded. macOS needs your password to install it on this Mac. It is used once and not stored."
        )),
        rows: Vec::new(),
        more: 0,
        callout: Some(Callout {
            tone: Tone::Warning,
            title: Some("Your Mac will restart in a few minutes".to_string()),
            text: "Installing starts as soon as you continue, and your Mac restarts by itself shortly after. It may not be \
                   able to close your apps first, so save your work now."
                .to_string(),
        }),
        progress: None,
        field: Some(Field { label: format!("Password for \u{201c}{username}\u{201d}"), placeholder: "Password".to_string() }),
        note: None,
        footer_note: Some("This is the last step, so this window stays open until you continue.".to_string()),
        buttons: vec![Button { label: "Install update".to_string(), style: ButtonStyle::Primary }],
        focused: 0,
    }
}

/// Escapes a string for embedding inside a double-quoted AppleScript string literal — this process
/// never controls all of the text it notifies with (application names ultimately come from the
/// backend, which in turn can include whatever a package manager or the AI reported), so this must
/// not be skippable.
fn escape(value: &str) -> String {
    value.replace('\\', "\\\\").replace('"', "\\\"")
}

/// A Notification Center banner: `title` in bold, `message` under it. Best-effort — a failed
/// notification (Notification Center unreachable, or somehow no user session) is never a reason to
/// abort patching.
pub fn notify(title: &str, message: &str) {
    crate::logging::info(&format!("notification: {title} — {message}"));
    let script = format!(r#"display notification "{}" with title "{}""#, escape(message), escape(title));
    let shown = Command::new("osascript").arg("-e").arg(&script).output().context("failed to run osascript").and_then(|output| {
        if output.status.success() {
            Ok(())
        } else {
            Err(anyhow::anyhow!("osascript exited with {}: {}", output.status, String::from_utf8_lossy(&output.stderr).trim()))
        }
    });
    if let Err(err) = shown {
        crate::logging::warn(&format!("could not show notification: {err:#}"));
    }
}

/// A crude but dependency-free progress indicator for the menu bar's status line, which is plain
/// text — rendered as a Unicode block bar.
pub fn progress_bar(completed: usize, total: usize) -> String {
    const WIDTH: usize = 20;
    let filled = if total == 0 { 0 } else { (completed * WIDTH) / total };
    let filled = filled.min(WIDTH);
    format!("[{}{}] {completed}/{total}", "█".repeat(filled), "░".repeat(WIDTH - filled))
}

#[cfg(test)]
mod tests {
    use super::*;

    fn names(names: &[&str]) -> Vec<String> {
        names.iter().map(|name| name.to_string()).collect()
    }

    fn labels(card: &Card) -> Vec<&str> {
        card.rows.iter().map(|row| row.label.as_str()).collect()
    }

    #[test]
    fn install_now_is_the_focused_primary_on_the_right() {
        let card = confirm_card("2 hours", 3, &names(&["Firefox"]), None);

        assert_eq!(card.buttons[1].label, "Install now", "index 1 is what confirm_patch reads as Patch Now");
        assert_eq!(card.buttons[1].style, ButtonStyle::Primary);
        assert_eq!(card.focused, 1);
        assert_eq!(card.buttons[0].label, "Remind me in 2 hours");
    }

    #[test]
    fn the_postpones_left_read_as_a_person_would_say_them() {
        assert!(confirm_card("2 hours", 3, &names(&["Firefox"]), None).note.unwrap().contains("postpone 3 more times"));
        assert!(confirm_card("1 day", 1, &names(&["Firefox"]), None).note.unwrap().contains("postpone 1 more time."));
    }

    #[test]
    fn the_confirm_prompt_lists_the_applications_that_will_close() {
        let card = confirm_card("1 hour", 3, &names(&["Firefox", "Slack"]), None);

        assert_eq!(labels(&card), vec!["Firefox", "Slack"]);
        assert!(card.body.unwrap().contains("will close"));
    }

    /// A host that has been offline for a while can have dozens pending; the window has to stay on
    /// screen, so past the cap the rest are counted rather than named.
    #[test]
    fn the_confirm_prompt_summarises_the_tail_of_a_long_list() {
        let all: Vec<String> = (1..=14).map(|n| format!("App {n}")).collect();
        let card = confirm_card("1 hour", 3, &all, None);

        assert_eq!(card.rows.len(), MAX_LISTED_APPS);
        assert_eq!(card.rows.last().unwrap().label, "App 10");
        assert_eq!(card.more, 4);
    }

    #[test]
    fn the_macos_update_is_named_and_a_restart_promised_only_when_it_is_in_the_plan() {
        let with_os = confirm_card("1 hour", 3, &names(&["Firefox"]), Some(Some("26.7")));
        let row = with_os.rows.last().unwrap();
        assert_eq!(row.label, "macOS 26.7");
        assert_eq!(row.badge.as_ref().unwrap().text, "Restarts your Mac");
        assert!(with_os.body.unwrap().contains("your Mac will restart"));

        let apps_only = confirm_card("1 hour", 3, &names(&["Firefox"]), None);
        assert!(apps_only.rows.iter().all(|row| row.badge.is_none()));
        assert!(!apps_only.body.unwrap().contains("restart"), "no macOS update, no promise of a restart");
    }

    #[test]
    fn a_macos_update_alone_still_says_what_it_is() {
        let card = confirm_card("1 hour", 3, &[], Some(None));

        assert_eq!(labels(&card), vec!["macOS update"]);
        assert!(card.body.unwrap().starts_with("Your Mac will restart"));
    }

    #[test]
    fn the_final_notice_names_what_will_close_and_the_restart() {
        let card = final_notice_card(5, &names(&["Firefox", "Slack"]), true);

        assert_eq!(card.title, "Updates will install in 5 minutes");
        let callout = card.callout.unwrap();
        assert_eq!(callout.title.as_deref(), Some("Save your work now"));
        assert!(callout.text.starts_with("Firefox and Slack will close, and your Mac will restart"), "{}", callout.text);
        assert_eq!(final_notice_card(1, &[], false).title, "Updates will install in 1 minute");
    }

    #[test]
    fn apps_are_named_the_way_a_sentence_would() {
        assert_eq!(name_the_apps(&[]), None);
        assert_eq!(name_the_apps(&names(&["Zoom"])).unwrap(), "Zoom");
        assert_eq!(name_the_apps(&names(&["Zoom", "Slack", "Chrome"])).unwrap(), "Zoom, Slack and Chrome");
        assert_eq!(name_the_apps(&names(&["Zoom", "Slack", "Chrome", "Teams"])).unwrap(), "Zoom, Slack and 2 other apps");
    }

    #[test]
    fn the_consent_prompt_names_who_is_asking_and_how_to_stop_it() {
        let card = remote_control_card("admin@example.com", &[], 60);

        assert!(card.body.as_deref().unwrap().contains("admin@example.com is asking"), "{card:?}");
        assert!(card.body.as_deref().unwrap().contains("keyboard and mouse"), "{card:?}");
        let callout = card.callout.unwrap().text;
        assert!(callout.contains("End Remote Session") && callout.contains("menu bar"), "{callout}");
    }

    #[test]
    fn the_consent_prompt_says_silence_is_a_refusal_and_lists_what_will_not_work() {
        let card = remote_control_card("admin@example.com", &["Your screen cannot be shared.".to_string()], 60);
        let note = card.note.unwrap();

        assert!(note.contains("closes in 1 minute and is declined"), "{note}");
        assert!(note.contains("Your screen cannot be shared."), "{note}");
    }

    #[test]
    fn deny_is_what_return_presses_and_allow_is_never_the_filled_button() {
        let card = remote_control_card("admin@example.com", &[], 60);

        assert_eq!(card.buttons[card.focused].label, "Deny");
        assert!(card.buttons.iter().all(|button| button.style != ButtonStyle::Primary));
        assert_eq!(card.buttons[1].label, "Allow", "index 1 is what confirm_remote_control reads as consent");
    }

    #[test]
    fn durations_read_as_a_person_would_say_them() {
        assert_eq!(duration(60), "1 minute");
        assert_eq!(duration(300), "5 minutes");
        assert_eq!(duration(90), "90 seconds");
    }

    /// The one thing somebody must not be able to miss before authorizing: `-R` means this Mac
    /// restarts by itself, minutes from now, possibly without closing anything first.
    #[test]
    fn the_password_prompt_warns_that_the_mac_restarts_itself_shortly() {
        let callout = install_password_card("david", Some("26.7")).callout.unwrap();

        assert_eq!(callout.title.as_deref(), Some("Your Mac will restart in a few minutes"));
        // No message argument: CodeQL reads anything formatted out of the password card as a leak.
        assert!(callout.text.contains("restarts by itself"));
        assert!(callout.text.contains("save your work"));
    }

    #[test]
    fn the_password_prompt_names_the_version_and_the_account_and_offers_no_way_out() {
        let card = install_password_card("david", Some("26.7"));

        assert_eq!(card.title, "Ready to install macOS 26.7");
        assert!(card.field.unwrap().label.contains("\u{201c}david\u{201d}"));
        assert_eq!(card.buttons.len(), 1, "one button, so there is nothing to cancel with");
        assert!(card.footer_note.unwrap().contains("stays open until you continue"));
    }

    #[test]
    fn the_password_prompt_stays_readable_when_the_version_is_unknown() {
        let card = install_password_card("david", None);

        assert_eq!(card.title, "Ready to install the macOS update");
        assert!(card.body.unwrap().starts_with("The macOS update has downloaded."));
    }

    #[test]
    fn escape_handles_quotes_and_backslashes() {
        assert_eq!(escape(r#"say "hi" \ bye"#), r#"say \"hi\" \\ bye"#);
    }
}
