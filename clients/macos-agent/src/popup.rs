//! The one look every window this agent puts in front of a user shares — the organisation's logo
//! and name (see `branding`), a bold title on white, Arial, a list or a callout, and pill buttons in
//! a grey footer — and the code that puts one on screen. `dialogs` builds a [`Card`] for each
//! question it asks, `progress_window` one for the running cycle; `popup.js` draws it.
//!
//! # Why a script, and not a window in this process
//!
//! These used to be AppleScript's `display dialog`: a generic caution icon, "Kintsugi Patching" in
//! the title, and wording like "delay this up to 8 more time(s)". A user's screenshot of exactly
//! that is what led here, after the Windows agent had already been given this look.
//!
//! The windows are drawn by AppKit through `osascript -l JavaScript`, which is what the macOS
//! password prompt already was, rather than by objc2 in this process, for three reasons:
//!
//! - **A deadline the wall clock keeps.** A question is a child process, so `wait_until` can take it
//!   down when its time is up even if the Mac slept through most of it — see `wait_until`. A window
//!   in this process would need its own timer, and NSTimer stops while the Mac sleeps, which is the
//!   very case the deadline exists for.
//! - **Nothing to share with the menu bar's thread.** Every modal question here is asked from the
//!   patch cycle's or the remote-control thread, and AppKit windows belong to the main thread, which
//!   is busy running the menu bar. A child process has its own main thread.
//! - **Its own window, so no TCC prompt.** The obvious AppleScript route to a front-most dialog
//!   sends Apple events to System Events, which TCC gates behind an Automation prompt per Mac. The
//!   script's window is its own, raised with `activateIgnoringOtherApps`.
//!
//! The card arrives as JSON in `argv`, never interpolated into the script's source, so nothing a
//! package manager or the server reported (an application's name, an administrator's) needs
//! escaping for JavaScript. Keep `popup.js` in step with the Windows agent's `popup.rs`, which
//! draws the same card with GDI.

use std::io::Write;
use std::process::{Child, ChildStdin, Command, Stdio};
use std::time::Duration;

use anyhow::{Context, Result};
use serde::Serialize;

use crate::branding::Branding;
use crate::schedule::now_epoch;

const SCRIPT: &str = include_str!("popup.js");

// ---------------------------------------------------------------------------------------------
// What a window says. Plain data, so `dialogs` can build and test one without a display. The field
// names are the ones `popup.js` reads.
// ---------------------------------------------------------------------------------------------

/// One window's content, top to bottom. Everything but the title is optional.
#[derive(Debug, Clone, PartialEq, Eq, Serialize)]
pub struct Card {
    /// The small capitals above the title: what kind of message this is ("Software updates").
    pub eyebrow: String,
    pub title: String,
    pub body: Option<String>,
    /// A bordered list, one line each — the applications an update will close, for instance.
    pub rows: Vec<Row>,
    /// How many further items the list stands for, drawn as "… and N more" after the last row
    /// without a badge.
    pub more: usize,
    pub callout: Option<Callout>,
    pub progress: Option<Progress>,
    /// A password box. A card with one answers with what was typed, never with a button index —
    /// see [`ask_password`].
    pub field: Option<Field>,
    /// Small grey text under everything else.
    pub note: Option<String>,
    /// Small grey text in the footer beside a single button.
    pub footer_note: Option<String>,
    /// Left to right. With two, the first sits at the left edge and the second at the right — the
    /// safe choice on the left and never the filled one, as the style guide's dialogs have it.
    pub buttons: Vec<Button>,
    /// Which button Return presses.
    pub focused: usize,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize)]
pub struct Row {
    pub label: String,
    pub badge: Option<Badge>,
}

/// A small tinted label at a row's right-hand end: "Restarts your Mac".
#[derive(Debug, Clone, PartialEq, Eq, Serialize)]
pub struct Badge {
    pub text: String,
    pub tone: Tone,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize)]
#[serde(rename_all = "lowercase")]
pub enum Tone {
    Info,
    Warning,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize)]
pub struct Callout {
    pub tone: Tone,
    pub title: Option<String>,
    pub text: String,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize)]
pub struct Progress {
    pub label: String,
    pub completed: usize,
    /// Zero while nothing has started, which draws an empty bar and no "2 of 5".
    pub total: usize,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize)]
pub struct Field {
    pub label: String,
    pub placeholder: String,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize)]
pub struct Button {
    pub label: String,
    pub style: ButtonStyle,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize)]
#[serde(rename_all = "lowercase")]
pub enum ButtonStyle {
    /// Filled with the action blue: the one thing the window is for.
    Primary,
    /// Blue outline: a real choice, but not the one being recommended.
    Outline,
    /// Grey outline: the safe choice.
    Neutral,
}

/// The card plus the branding it is drawn with: what `popup.js` receives.
#[derive(Serialize)]
struct Shown<'a> {
    window_title: &'a str,
    logo: Option<&'a str>,
    #[serde(flatten)]
    card: &'a Card,
}

fn to_json(card: &Card, window_title: &str, logo: Option<&str>) -> Result<String> {
    serde_json::to_string(&Shown { window_title, logo, card }).context("could not encode the window's contents")
}

fn script(json: &str) -> Command {
    let mut command = Command::new("osascript");
    command.args(["-l", "JavaScript", "-e", SCRIPT]).arg(json);
    command
}

// ---------------------------------------------------------------------------------------------
// Asking.
// ---------------------------------------------------------------------------------------------

/// How often a question's process is checked, for an answer and against its wall-clock deadline.
/// It is also how long a click can wait before the agent acts on it — the window closes at once,
/// but the progress window, or a remote-control session, starts only when this notices — so a
/// second rather than the five this was when only the deadline was in mind. Each check is one
/// `try_wait`, so there is nothing to save by making it larger.
const DEADLINE_POLL: Duration = Duration::from_secs(1);

/// Shows `card` until a button is pressed, or until `timeout_seconds` of wall-clock time have
/// passed. `Some(index)` is the button, `None` that nobody answered in time — every caller decides
/// what silence means, because for the patch prompt it is a delay and for remote control a refusal.
pub fn choose(card: &Card, timeout_seconds: u64) -> Result<Option<usize>> {
    let branding = Branding::current();
    let json = to_json(card, branding.window_title(), branding.logo_for_popup().as_deref())?;
    let child = script(&json)
        .stdin(Stdio::null())
        .stdout(Stdio::piped())
        .stderr(Stdio::piped())
        .spawn()
        .context("failed to run osascript")?;

    match wait_until(child, now_epoch() + timeout_seconds, DEADLINE_POLL)? {
        ChildRun::Exited(output) => {
            if !output.status.success() {
                anyhow::bail!("osascript exited with {}: {}", output.status, String::from_utf8_lossy(&output.stderr).trim());
            }
            let answer = String::from_utf8_lossy(&output.stdout);
            let index = answer.trim().parse::<usize>().with_context(|| format!("the window answered {answer:?}, not a button"))?;
            Ok(Some(index))
        }
        ChildRun::Killed => Ok(None),
    }
}

/// Shows a card with a [`Field`] and returns what was typed. There is no deadline and no way to
/// dismiss it without typing something — see `dialogs::request_install_password` for why — so the
/// only other way out is an `Err`: osascript could not be run, or there was no window server.
///
/// What was typed reaches this process on osascript's stdout and is never logged. Only the one
/// newline osascript adds after a result comes off it: a password may begin or end with a space,
/// and trimming would hand `softwareupdate` a different password than the one typed, which arrives
/// as "Failed to authenticate", indistinguishable from a wrong one.
pub fn ask_password(card: &Card) -> Result<String> {
    let branding = Branding::current();
    let json = to_json(card, branding.window_title(), branding.logo_for_popup().as_deref())?;
    let output = script(&json).stdin(Stdio::null()).output().context("failed to run osascript")?;
    if !output.status.success() {
        anyhow::bail!("osascript exited with {}: {}", output.status, String::from_utf8_lossy(&output.stderr).trim());
    }
    let stdout = String::from_utf8_lossy(&output.stdout);
    let typed = strip_result_newline(&stdout);
    if typed.is_empty() {
        // The script does not return until it holds a non-empty answer, so this is osascript
        // printing nothing at all — never something to hand on as a password.
        anyhow::bail!("the window closed without an answer");
    }
    Ok(typed.to_string())
}

/// Removes the one trailing newline `osascript` prints after a script's result, and nothing else.
fn strip_result_newline(stdout: &str) -> &str {
    stdout.strip_suffix('\n').unwrap_or(stdout)
}

/// How `wait_until` saw a child end.
enum ChildRun {
    Exited(std::process::Output),
    Killed,
}

/// Waits for `child` to exit, or kills it once the wall clock reaches `deadline_epoch`, whichever
/// comes first — which takes its window down with it.
///
/// The wall clock, because nothing else keeps time across a sleep. AppleScript's `giving up after`,
/// which these dialogs used to rely on, measures time the Mac spent running: on this fleet's own
/// Mac a confirm dialog raised with a one-hour giveup on a Friday morning was still standing on the
/// Monday, and gave up seventy-one hours after it went up. The delay budget the policy describes is
/// meant to run from the moment the person is asked, whether or not the lid is shut in between, and
/// `Instant` stops during sleep too, hence `now_epoch`. The deadline is checked before each sleep
/// rather than after, so one that has already passed — the wake-from-sleep case — is acted on at
/// once.
fn wait_until(mut child: Child, deadline_epoch: u64, poll: Duration) -> Result<ChildRun> {
    loop {
        if child.try_wait().context("could not poll the window's process")?.is_some() {
            let output = child.wait_with_output().context("could not read the window's answer")?;
            return Ok(ChildRun::Exited(output));
        }
        if now_epoch() >= deadline_epoch {
            // A kill that fails because the child exited in between is not a problem: the wait
            // reaps it either way, and the deadline had passed, so "nobody answered" stands.
            let _ = child.kill();
            let _ = child.wait();
            return Ok(ChildRun::Killed);
        }
        std::thread::sleep(poll);
    }
}

// ---------------------------------------------------------------------------------------------
// The progress window: a card that stays up and changes.
// ---------------------------------------------------------------------------------------------

/// A window that shows a card without waiting for an answer, and takes a new one with [`update`].
/// Its one button hides it until the next update, inside the script, with nothing to report back.
/// Dropping it closes the window: the script exits when its stdin closes, and is killed besides.
///
/// [`update`]: Modeless::update
pub struct Modeless {
    child: Child,
    stdin: ChildStdin,
    window_title: String,
    logo: Option<String>,
}

impl Modeless {
    pub fn open(card: &Card) -> Result<Self> {
        // Branding read once, for the window's life: every update redraws the header, and a
        // progress window that changed its logo halfway through would be stranger than one that
        // was a few minutes behind an administrator's change.
        let branding = Branding::current();
        let window_title = branding.window_title().to_string();
        let logo = branding.logo_for_popup();
        let mut child = script(&to_json(card, &window_title, logo.as_deref())?)
            .arg("modeless")
            .stdin(Stdio::piped())
            .stdout(Stdio::null())
            .stderr(Stdio::null())
            .spawn()
            .context("failed to run osascript")?;
        let stdin = child.stdin.take().context("osascript has no stdin")?;
        Ok(Modeless { child, stdin, window_title, logo })
    }

    /// Replaces the card and brings the window back if it was hidden. An `Err` means the script is
    /// gone, and the caller opens a new one.
    pub fn update(&mut self, card: &Card) -> Result<()> {
        let line = to_json(card, &self.window_title, self.logo.as_deref())?;
        self.stdin.write_all(format!("{line}\n").as_bytes()).context("the progress window has closed")?;
        self.stdin.flush().context("the progress window has closed")
    }
}

impl Drop for Modeless {
    fn drop(&mut self) {
        let _ = self.child.kill();
        let _ = self.child.wait();
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn card() -> Card {
        Card {
            eyebrow: "Software updates".to_string(),
            title: "Updates are ready to install".to_string(),
            body: None,
            rows: vec![Row { label: "macOS 27.2.1".to_string(), badge: Some(Badge { text: "Restarts your Mac".to_string(), tone: Tone::Warning }) }],
            more: 0,
            callout: None,
            progress: None,
            field: None,
            note: None,
            footer_note: None,
            buttons: vec![Button { label: "Install now".to_string(), style: ButtonStyle::Primary }],
            focused: 0,
        }
    }

    /// The names `popup.js` reads. A rename on this side that the script did not follow would draw
    /// an empty window, and nothing would fail to compile.
    #[test]
    fn the_card_reaches_the_script_under_the_names_it_reads() {
        let json: serde_json::Value = serde_json::from_str(&to_json(&card(), "Example IT", Some("bG9nbw==")).unwrap()).unwrap();

        assert_eq!(json["window_title"], "Example IT");
        assert_eq!(json["logo"], "bG9nbw==");
        assert_eq!(json["eyebrow"], "Software updates");
        assert_eq!(json["rows"][0]["badge"]["tone"], "warning");
        assert_eq!(json["buttons"][0]["style"], "primary");
        assert_eq!(json["focused"], 0);
        for name in ["body", "more", "callout", "progress", "field", "note", "footer_note"] {
            assert!(json.get(name).is_some(), "{name} is missing");
            assert!(SCRIPT.contains(&format!("card.{name}")) || SCRIPT.contains(&format!(".{name}")), "popup.js never reads {name}");
        }
    }

    #[test]
    fn strip_result_newline_removes_only_the_newline_osascript_adds() {
        assert_eq!(strip_result_newline(" pass word \n"), " pass word ");
        assert_eq!(strip_result_newline("   \n"), "   ");
        assert_eq!(strip_result_newline("no newline"), "no newline");
    }

    /// The wake-from-sleep case: a deadline that has already passed is acted on at the first
    /// check, before any sleep, so a stale prompt comes down within a poll of the Mac waking.
    #[test]
    fn wait_until_kills_a_child_whose_deadline_has_already_passed() {
        let child = Command::new("sleep").arg("30").stdout(Stdio::piped()).spawn().unwrap();
        let started = std::time::Instant::now();

        let run = wait_until(child, now_epoch() - 1, Duration::from_secs(60)).unwrap();

        assert!(matches!(run, ChildRun::Killed));
        assert!(started.elapsed() < Duration::from_secs(5));
    }

    #[test]
    fn wait_until_returns_the_output_of_a_child_that_exits_before_its_deadline() {
        let child = Command::new("echo").arg("1").stdout(Stdio::piped()).spawn().unwrap();

        match wait_until(child, now_epoch() + 60, Duration::from_millis(10)).unwrap() {
            ChildRun::Exited(output) => assert_eq!(String::from_utf8_lossy(&output.stdout).trim(), "1"),
            ChildRun::Killed => panic!("the child exited on its own"),
        }
    }
}
