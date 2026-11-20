use std::time::Duration;

use crate::config;
use crate::dialogs::{self, ConfirmChoice};
use crate::logging;
use crate::os_update;
use crate::presence;
use crate::policy::PatchingPolicy;
use crate::queue::{self, Plan, RequestKind};
use crate::running_apps;
use crate::schedule::{self, ScheduleState};
use crate::status::{AgentStatus, StatusReporter};

/// How much notice an automatic start gives, *in total* — the "no delays left" dialog is part of
/// it rather than something served before it, so a host nobody is sitting at waits five minutes
/// from that dialog appearing to patching starting, not five plus however long the dialog stood
/// there. `confirm_or_delay` measures what the dialog used and hands `execute` the remainder.
const WARNING_PERIOD: Duration = Duration::from_secs(5 * 60);

/// The shortest remainder still worth waiting out — see `remaining_warning`.
const MINIMUM_WARNING: Duration = Duration::from_secs(30);

/// What is left of the notice after the "no delays left" dialog has stood there for `stood_for`.
/// This is the rule that keeps an automatic start's warning five minutes *in total* rather than
/// five on top of however long that dialog was up, so somebody who reads it and clicks OK still
/// gets the rest of the period to save their work and a host with nobody at it waits five minutes
/// rather than ten.
///
/// A remainder under `MINIMUM_WARNING` comes back as none at all: when the dialog runs the period
/// out on its own the leftover is a second or two of rounding, and a notification promising "1
/// minute" that is really four seconds says less than saying nothing. Kept identical in the other
/// two agents.
fn remaining_warning(stood_for: Duration) -> Duration {
    let remaining = WARNING_PERIOD.saturating_sub(stood_for);
    if remaining < MINIMUM_WARNING {
        Duration::ZERO
    } else {
        remaining
    }
}

/// How long to wait for the service to answer a `Plan` request. Short — it's one HTTP call plus a
/// Windows Update search — but not so short that a slow link makes a due cycle silently vanish.
const PLAN_TIMEOUT: Duration = Duration::from_secs(3 * 60);

/// How long to wait for one application's upgrade. Generous: a large installer over a slow link,
/// downloaded and then run silently, legitimately takes a while.
const APP_PATCH_TIMEOUT: Duration = Duration::from_secs(30 * 60);

/// How long to wait for the service to start a restart. It only has to launch `shutdown.exe`.
const RESTART_TIMEOUT: Duration = Duration::from_secs(60);

/// How long to wait for the Windows Update install. Windows updates can legitimately take a very
/// long time to download and install, so this matches the macOS agent's own OS-update budget.
const OS_UPDATE_TIMEOUT: Duration = Duration::from_secs(60 * 60);

/// Asks the service what's actually pending. Kept separate from, and always run *before*, any
/// dialog — a confirm prompt (or the "no delays left" acknowledgement) has no business appearing if
/// the server can't even be reached to say whether there's anything to patch, or if it says there
/// isn't.
///
/// The macOS agent computes this itself; here it's a queue round trip, because the tray process
/// holds no mutual-TLS identity and so cannot ask the server anything directly. See `queue`.
fn plan() -> anyhow::Result<Plan> {
    let result = queue::submit(&config::queue_dir(), RequestKind::Plan, "", PLAN_TIMEOUT)?;
    if !result.success {
        anyhow::bail!("the agent service could not determine what needs patching: {}", result.output);
    }
    result.data.ok_or_else(|| anyhow::anyhow!("the agent service answered a plan request with no plan"))
}

/// Runs one full due patch cycle: check what's actually pending, confirm (or delay) only if there's
/// real work, a 5-minute warning unless the user asked for patching to start now, applications, then
/// the OS if an update is available — in that order, per the policy. `report` is how the notification-area menu (which this module knows
/// nothing about) is kept in sync with what's happening.
///
/// Returns without doing anything destructive if the service can't be reached, if there's nothing
/// to patch, or if the user chooses to delay — in every case, the next poll tick (or the next due
/// time) picks it back up; none of these are treated as failures.
pub fn run(policy: &PatchingPolicy, state: &mut ScheduleState, report: &StatusReporter) {
    let work = match plan() {
        Ok(work) => work,
        Err(err) => {
            // Deliberately no dialog and no state change here: the server being briefly unreachable
            // (a deploy, a network blip) isn't something the user needs to be interrupted about,
            // and it costs nothing to just try again at the next poll tick.
            logging::warn(&format!("could not check for pending patches, will retry at the next check: {err:#}"));
            return;
        }
    };
    let work = work.without_actions();

    // Whatever nobody has open is patched before anybody is asked anything — see `running_apps`.
    // Only what is open, and the Windows update (which can want a restart), is left for the
    // prompt; when that is nothing, the cycle completes without one.
    // Only on a cycle's first ask. After a Delay or an unanswered prompt this runs again every delay
    // period, and an application that failed while shut would be patched, and reported failed,
    // each time: the repetition `SilentAttempts` exists to stop. What closes later is the silent
    // pass's; what is shut here goes to the prompt, as every row did before.
    let work = if state.has_been_delayed() { work } else { patch_shut_applications(work, report) };

    if work.is_empty() {
        logging::info("nothing left to patch that needs asking about at this check");
        state.register_completed(policy);
        report(AgentStatus::Idle { next_due_epoch: state.next_due_epoch() });
        return;
    }

    let warning = match confirm_or_delay(policy, state, &work.app_names(), work.os_update_available, report) {
        Ok(Decision::PatchNow) => Duration::ZERO,
        Ok(Decision::ProceedAfterWarning { remaining }) => remaining,
        // Both delaying answers stop here: `confirm_or_delay` has already moved the due time, and
        // the next tick picks the cycle back up — at once for an unanswered dialog, a delay period
        // later for an explicit "Delay".
        Ok(Decision::Delayed) | Ok(Decision::Unanswered) => return,
        Err(err) => {
            logging::warn(&format!("could not show the patching confirmation dialog, will retry at the next check: {err:#}"));
            return;
        }
    };

    execute(policy, state, work, report, warning, true, false, false);
}

/// Patches every application in `work` the service found shut, silently, and returns `work` with
/// only what is left: the applications that were open (or might have been — `running_apps` answers
/// "could not tell" as open) and the Windows update, untouched.
///
/// Goes by the service's answer in the plan (`PlannedApp::open`), since this process cannot see
/// every process on the host; the service's own silent pass is the one that re-checks before each
/// application. The menu is told, as `PatchingInBackground`, but the progress window stays shut and
/// no notification is posted. Kept identical in the other two agents.
fn patch_shut_applications(work: Plan, report: &StatusReporter) -> Plan {
    let Plan { apps, os_update_available } = work;
    let (shut, open) = running_apps::partition(apps, |app| app.open);
    let queue_dir = config::queue_dir();

    for app in shut {
        report(AgentStatus::PatchingInBackground { current: app.application_name.clone() });
        logging::info(&format!("{} is not open, so asking the agent service to patch it without asking", app.application_name));
        match queue::submit(&queue_dir, RequestKind::AppPatch, &app.application_name, APP_PATCH_TIMEOUT) {
            Ok(result) if result.success => logging::info(&format!("patched {} successfully", app.application_name)),
            Ok(result) => logging::error(&format!("failed to patch {}: {}", app.application_name, result.output)),
            Err(err) => logging::error(&format!("failed to patch {}: {err:#}", app.application_name)),
        }
    }

    Plan { apps: open, os_update_available }
}

/// The menu's "Patch Now" item: skips the confirm/delay decision altogether, since asking whether to
/// delay makes no sense when the user just explicitly asked to patch right now, and goes straight
/// into patching once it's confirmed there's actually something to do. It also skips the 5-minute
/// warning, for the reason [`Decision`] gives — the same reason the dialog's own "Patch Now" button
/// does.
pub fn run_now(policy: &PatchingPolicy, state: &mut ScheduleState, report: &StatusReporter) {
    logging::info("Patch Now triggered manually from the notification area");

    let work = match plan() {
        Ok(work) => work,
        Err(err) => {
            logging::warn(&format!("could not check for pending patches: {err:#}"));
            dialogs::notify("Couldn't check for updates", "Try again in a few minutes. If it keeps happening, contact IT.");
            return;
        }
    };
    let work = work.without_actions();

    if work.is_empty() {
        dialogs::notify("You're up to date", "There are no updates to install right now.");
        state.register_completed(policy);
        report(AgentStatus::Idle { next_due_epoch: state.next_due_epoch() });
        return;
    }

    execute(policy, state, work, report, Duration::ZERO, true, false, false);
}

/// An administrator's "patch this now", collected from the server by the scheduler loop — see
/// `forced_patch_run::collect` and `queue::RequestKind::ForcedPatchRuns`. This is the emergency
/// path: a named application is patched on this host at the next poll tick rather than at this
/// host's next scheduled cycle.
///
/// It is the third entry point rather than a flag on one of the other two, because it answers the
/// confirm/delay question and the warning question differently from both:
///
/// - **No confirm/delay dialog.** That dialog exists to let the person at the desk move an
///   *automatic* cycle out of the way. An administrator forcing a run has already decided the
///   emergency outranks the interruption, so offering "Delay" would be offering something the
///   answer to has already been given.
/// - **The full five-minute warning, all the same.** `run_now` skips it because the click came from
///   the very person the interruption falls on (see [`Decision`]); that reasoning does not hold
///   here, where the person deciding is somewhere else entirely. So the host gets the same notice a
///   scheduled cycle gives — "Updates start in 5 minutes. Save your work." — with
///   nothing to click.
/// - **The schedule is not touched.** A forced run patches one application; the scheduled cycle
///   patches everything. Registering this as a completed cycle would push the real one a whole
///   interval into the future, so an emergency patch would silently cost this host its next
///   ordinary one. `execute`'s `reschedule` argument is what says so.
///
/// Kept identical in the other two agents.
pub fn run_forced(policy: &PatchingPolicy, state: &mut ScheduleState, report: &StatusReporter, application_names: &[String]) {
    logging::info(&format!("running a forced patch cycle for: {}", application_names.join(", ")));

    let work = match plan() {
        Ok(work) => work,
        Err(err) => {
            // Nothing is lost by giving up quietly here *except* the instruction itself: the server
            // has already marked it collected, so the next tick will not bring it back. Said at
            // warn level for that reason — this is the one branch where "try again later" is not
            // true and an administrator may be waiting on a patch that is not coming.
            logging::warn(&format!("could not check what to patch for a forced run, so it has been dropped: {err:#}"));
            return;
        }
    };

    // An administrator's "patch this host now" from Patch Health arrives as a forced run named for the
    // operating system rather than for an application, and it is the one forced run that installs
    // the Windows update. `narrowed_to` drops that update on purpose for an application's run, so it
    // is put back here, and only here.
    let os_requested = application_names.iter().any(|name| name.eq_ignore_ascii_case(os_update::OS_FAILURE_APPLICATION_NAME));
    let os_pending = work.os_update_available;
    let mut work = work.narrowed_to(application_names);
    work.os_update_available = os_requested && os_pending;

    if work.is_empty() {
        // Not a failure, and deliberately not a notification: the usual cause is that the host is
        // already current on the application, or that its script is unsigned and therefore not
        // runnable at all (`upgrade::is_patchable`). Neither is something to interrupt the person
        // at this desk about for an instruction they did not raise.
        logging::info("forced patch run has nothing to do on this host — nothing patchable matched");
        return;
    }

    // Whether anyone in this session would see the warning. A screen that is locked, off or behind
    // a screen saver (what `presence` reads) would show a "save your work" notification to nobody,
    // so five minutes of it would only delay the install. Installing closes nothing, so skipping a
    // notice nobody would read costs nothing.
    //
    // That is *only* the warning before the install. The restart after it is decided separately,
    // after the install, by the service across every session on the host (see `request_restart` and
    // `queue::restart_decision`): a locked screen is somebody with work open, not an empty desk.
    //
    // Only for the operating system run. An application's forced run keeps its warning whatever the
    // screen is doing, exactly as before this existed.
    let (warning, restart_after_os) = if work.os_update_available {
        let obstruction = presence::obstruction();
        if let Some(reason) = &obstruction {
            logging::info(&format!("nobody would see a warning here ({reason}), so the forced Windows update starts without one"));
        }
        (forced_os_warning(obstruction.is_some()), true)
    } else {
        (WARNING_PERIOD, false)
    };

    // A run that is only software titles is silent, with no warning: an install or uninstall the
    // administrator assigned is meant to happen as soon as possible with no prompt, exactly as the
    // service's hourly check-in would have done it — "Run now" only brings it forward.
    let silent = work.is_only_actions();
    let warning = if silent { Duration::ZERO } else { warning };

    execute(policy, state, work, report, warning, false, restart_after_os, silent);
}

/// The warning before a forced Windows update. Somebody who would see it gets the full five minutes;
/// a screen nobody would see it on gets none. Says nothing about the restart, which is not decided
/// until the install has finished.
fn forced_os_warning(nobody_would_see_it: bool) -> Duration {
    if nobody_would_see_it {
        Duration::ZERO
    } else {
        WARNING_PERIOD
    }
}

/// Whether the tray asks for a restart at all, re-read from this session right before asking rather
/// than once before an install that can take an hour. A screen that is locked, off or behind a screen
/// saver means the countdown would be shown to nobody while their open work is closed. The service
/// applies its own, wider check across every session on top of this one.
fn should_ask_for_restart(obstruction: Option<&str>) -> bool {
    obstruction.is_none()
}

fn execute(
    policy: &PatchingPolicy,
    state: &mut ScheduleState,
    work: Plan,
    report: &StatusReporter,
    warning: Duration,
    // Whether finishing counts as this host's scheduled patch cycle. True for the two whole-host
    // cycles; false for a forced run, which patches one named application and must not push the
    // real cycle an interval into the future — see `run_forced`.
    reschedule: bool,
    // Whether to ask the service to restart the host *if Windows says the update it just installed
    // needs one*. Set only by a forced operating system run; a scheduled cycle's Windows update is
    // left to restart the way it always has.
    restart_after_os: bool,
    // A forced run that is only software titles: no progress window and no summary notification —
    // see `run_forced`. The warning is already zero for one.
    silent: bool,
) {
    if !warning.is_zero() {
        let minutes = (warning.as_secs() + 59) / 60;
        let title = format!("Updates start in {minutes} minute{}", if minutes == 1 { "" } else { "s" });
        let body = match dialogs::name_the_apps(&work.app_names()) {
            Some(names) => format!("Save your work. {names} will close."),
            None => "Save your work. Windows may need to restart.".to_string(),
        };
        dialogs::notify(&title, &body);
        report(AgentStatus::Patching { current: format!("{title}. Save your work."), completed: 0, total: 0 });
        std::thread::sleep(warning);
    }

    // No notification here: the progress window opens with the first step and says the same.
    logging::info("patch cycle starting");

    let (succeeded, failed) = run_patches(work, report, restart_after_os, silent);

    let (title, body) = summary(succeeded, failed);
    if !silent {
        dialogs::notify(&title, &body);
    }
    logging::info(&format!("patch cycle finished: {succeeded} succeeded, {failed} failed"));

    if reschedule {
        state.register_completed(policy);
    }
    report(AgentStatus::Idle { next_due_epoch: state.next_due_epoch() });
}

/// The notification at the end of a cycle. A failure does not say "check the logs", which is
/// advice for an administrator shown to somebody who cannot act on it, and does not promise that IT
/// already knows: a script that ran and failed is reported (`upgrade::report_patch_failure`), but a
/// step that never ran is not.
fn summary(succeeded: usize, failed: usize) -> (String, String) {
    let updates = |count: usize| format!("{count} update{}", if count == 1 { "" } else { "s" });
    if failed == 0 {
        let verb = if succeeded == 1 { "was" } else { "were" };
        return ("Updates installed".to_string(), format!("{} {verb} installed.", updates(succeeded)));
    }
    let installed = if succeeded > 0 { format!("{} installed. ", updates(succeeded)) } else { String::new() };
    (
        format!("{} didn't install", updates(failed)),
        format!("{installed}They'll be tried again next time. If this keeps happening, contact IT."),
    )
}

/// What the confirm-or-delay dialog settled on.
///
/// Whether the five-minute warning comes first turns on one question: is patching starting because
/// a person just asked for it, or because this host's schedule ran out of patience? The warning is
/// notice before an *automatic* start, so a click on "Patch Now" — which is that notice, given by
/// the very person the interruption falls on — starts patching there and then.
///
/// The two delaying variants differ in what the delay costs. An explicit "Delay" buys a fresh
/// period from now; an unanswered dialog has already spent one waiting for the answer, so it
/// counts down the budget without postponing anything further. Kept identical in the other two
/// agents.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
enum Decision {
    /// The user clicked "Patch Now": start immediately, with no warning and no further notice.
    PatchNow,
    /// There were no delays left to offer, so the acknowledgement has already been shown and
    /// patching proceeds. `remaining` is what is left of `WARNING_PERIOD` after however long that
    /// dialog stood there, which is what keeps the notice five minutes in total.
    ProceedAfterWarning { remaining: Duration },
    /// The user asked for more time. Nothing happens until the new due time arrives.
    Delayed,
    /// Nobody answered before the dialog gave up. Still a delay — the user was asked and said
    /// nothing — but the cycle is left due at once, so the next poll tick re-asks with the count
    /// decremented, and the budget running out lands on the branch above. See
    /// `ScheduleState::register_unanswered_prompt`.
    Unanswered,
}

impl Decision {
    /// Reads the dialog's answer as a decision. Split out from `confirm_or_delay` — which cannot
    /// be tested without a display — purely so the polarity of a timeout is pinned by a test
    /// rather than by a comment.
    fn from_choice(choice: ConfirmChoice) -> Self {
        match choice {
            // The click *is* the notice the warning exists to give, and it came from the one
            // person the interruption falls on — so patching starts now, as asked.
            ConfirmChoice::PatchNow => Decision::PatchNow,
            ConfirmChoice::Delay => Decision::Delayed,
            ConfirmChoice::TimedOut => Decision::Unanswered,
        }
    }
}

/// Asks whether this cycle proceeds, and on whose say-so — see [`Decision`] for what each answer
/// means and why an unanswered dialog is not a delay.
fn confirm_or_delay(
    policy: &PatchingPolicy,
    state: &mut ScheduleState,
    app_names: &[String],
    os_update_available: bool,
    report: &StatusReporter,
) -> anyhow::Result<Decision> {
    // Either dialog below stands there until it is answered, and the confirm one for a whole
    // delay period — so the menu is told a prompt is up, which greys its "Patch Now" item rather
    // than leaving it offering to ask a question that is on screen being asked. The cycle runs on
    // its own thread (`main::spawn_cycle`), so the scheduler is free to serve the menu meanwhile.
    report(AgentStatus::AwaitingAnswer);

    if !state.can_delay(policy) {
        // This dialog *is* the warning, not a preamble to it: it states the period and stands for
        // as much of it as the user leaves it up, and `execute` waits out whatever `remaining_warning`
        // says is left.
        let shown_at = schedule::now_epoch();
        dialogs::final_notice(WARNING_PERIOD.as_secs() / 60, app_names, WARNING_PERIOD.as_secs())?;
        let stood_for = Duration::from_secs(schedule::now_epoch().saturating_sub(shown_at));
        return Ok(Decision::ProceedAfterWarning { remaining: remaining_warning(stood_for) });
    }

    // Stamped before the dialog rather than after, because how long it stood there is what the
    // delay budget is charged for — including any of it the machine spent asleep.
    let shown_at = schedule::now_epoch();
    let choice = dialogs::confirm_patch(
        &policy.delay_label(),
        state.delays_remaining(policy),
        app_names,
        os_update_available,
        policy.delay_seconds(),
    )?;

    let decision = Decision::from_choice(choice);
    match decision {
        Decision::Delayed => {
            state.register_delay(policy);
            report(AgentStatus::Idle { next_due_epoch: state.next_due_epoch() });
        }
        // Nothing is reported here: the cycle is due again immediately, so the next tick either
        // re-asks or finds the budget gone and proceeds. Telling the menu "next patch due: now"
        // for the few seconds in between would say less than the line already there.
        Decision::Unanswered => state.register_unanswered_prompt(policy, shown_at),
        Decision::PatchNow | Decision::ProceedAfterWarning { .. } => {}
    }

    Ok(decision)
}

/// Applications first, then the OS — per the policy's intent, application updates are the frequent,
/// low-risk case, while an OS update is the more disruptive one (likely to need a restart) best
/// left until everything else is already current. Returns (succeeded, failed) counts across both.
///
/// Every step here is a queue round trip: this process is deliberately not privileged enough to
/// install anything itself, and reporting each result back to the server is the service's job too
/// (it's the side holding the identity). See `queue`.
fn run_patches(work: Plan, report: &StatusReporter, restart_after_os: bool, silent: bool) -> (usize, usize) {
    let Plan { apps, os_update_available } = work;
    let total = apps.len() + usize::from(os_update_available);
    let queue_dir = config::queue_dir();
    let mut completed = 0;
    let mut succeeded = 0;
    let mut failed = 0;

    // One notification per application used to go out here as well, with a text progress bar in
    // it. The progress window already shows each step, so they only stacked up in the
    // notification centre.
    for app in &apps {
        let target = match &app.latest_version {
            Some(version) => format!("Updating {} to {version}", app.application_name),
            None => format!("Updating {}", app.application_name),
        };
        if !silent {
            report(AgentStatus::Patching { current: target, completed, total });
        }

        // A software title waits as long as the service lets its script run, which is longer than
        // an upgrade — the same request on the wire either way. See `upgrade::ACTION_WAIT`.
        let wait = if app.action.is_some() { crate::upgrade::ACTION_WAIT } else { APP_PATCH_TIMEOUT };
        logging::info(&format!("asking the agent service to patch {}", app.application_name));
        match queue::submit(&queue_dir, RequestKind::AppPatch, &app.application_name, wait) {
            Ok(result) if result.success => {
                succeeded += 1;
                logging::info(&format!("patched {} successfully", app.application_name));
            }
            Ok(result) => {
                failed += 1;
                logging::error(&format!("failed to patch {}: {}", app.application_name, result.output));
            }
            Err(err) => {
                failed += 1;
                logging::error(&format!("failed to patch {}: {err:#}", app.application_name));
            }
        }
        completed += 1;
    }

    if os_update_available {
        let current = "Installing Windows updates. This may take a while.".to_string();
        report(AgentStatus::Patching { current, completed, total });

        logging::info("asking the agent service to install Windows updates");
        match queue::submit(&queue_dir, RequestKind::OsUpdate, "", OS_UPDATE_TIMEOUT) {
            Ok(result) if result.success => {
                succeeded += 1;
                logging::info("Windows updates installed successfully");

                // A forced run restarts the host when Windows says the update needs it, so the update
                // is actually finished. Not otherwise: most updates, a Defender definitions update
                // above all, never need one, and restarting a person's computer for a signature file
                // is a cost with nothing bought. Asked of the service, which is the side allowed to —
                // and which decides for itself whether and when, across every session on the host:
                // see `queue::RequestKind::Restart`. Windows shows its own countdown.
                if restart_after_os && queue::reboot_required(&result) {
                    request_restart(&queue_dir);
                }
            }
            Ok(result) => {
                failed += 1;
                logging::error(&format!("Windows update install reported failure: {}", result.output));
            }
            Err(err) => {
                failed += 1;
                logging::error(&format!("could not install Windows updates: {err:#}"));
            }
        }
    }

    (succeeded, failed)
}

/// Asks the service to restart after a forced Windows update, unless this session's own screen says
/// nobody would see the countdown. Whatever happens, the person is told what state the host is in.
fn request_restart(queue_dir: &std::path::Path) {
    const RESTART_LATER: (&str, &str) =
        ("Restart to finish updating", "Windows updates are installed. Restart your computer when it suits you to finish.");

    let obstruction = presence::obstruction();
    if !should_ask_for_restart(obstruction.as_deref()) {
        logging::info(&format!(
            "not restarting after the Windows update: {}, so nobody would see the countdown",
            obstruction.unwrap_or_default()
        ));
        dialogs::notify(RESTART_LATER.0, RESTART_LATER.1);
        return;
    }

    // The body is ignored by this service version, which picks the delay itself; a service from
    // before 0.12.7 still needs one, and the full notice is the only one this tray ever asks for.
    match queue::submit(queue_dir, RequestKind::Restart, &queue::RESTART_DELAY_ATTENDED.to_string(), RESTART_TIMEOUT) {
        Ok(restart) if restart.success && queue::restart_deferred(&restart) => {
            logging::info(&format!("the agent service did not restart after the Windows update: {}", restart.output));
            dialogs::notify(RESTART_LATER.0, RESTART_LATER.1);
        }
        Ok(restart) if restart.success => {
            // Windows shows its own countdown beside this; the notification says why it is coming.
            dialogs::notify("Restarting to finish updating", "Windows will restart your computer shortly. Save your work.");
        }
        Ok(restart) => logging::error(&format!("could not start the restart after the Windows update: {}", restart.output)),
        Err(err) => logging::error(&format!("could not ask the agent service to restart after the Windows update: {err:#}")),
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn planned(name: &str, action: Option<&str>) -> queue::PlannedApp {
        queue::PlannedApp { application_name: name.to_string(), action: action.map(str::to_string), ..Default::default() }
    }

    /// A scheduled cycle never runs a software title: those belong to the service's hourly pass.
    /// Dropped before `is_empty`, so a host whose only pending work is an install is never asked to
    /// confirm anything. Pinned in all three agents.
    #[test]
    fn a_scheduled_cycle_drops_action_items_before_deciding_whether_there_is_anything_to_do() {
        let only_action = Plan { apps: vec![planned("Greenshot", Some("Install"))], os_update_available: false };
        assert!(only_action.without_actions().is_empty());

        let mixed = Plan { apps: vec![planned("Greenshot", Some("Install")), planned("Firefox", None)], os_update_available: false };
        assert_eq!(mixed.without_actions().app_names(), vec!["Firefox".to_string()]);
    }

    /// "Run now" is a forced run: it keeps the title it names, and is silent when that is all.
    #[test]
    fn a_forced_run_keeps_the_action_it_names_and_is_silent_when_that_is_all() {
        let plan = Plan { apps: vec![planned("Greenshot", Some("Install")), planned("Firefox", None)], os_update_available: true };

        let narrowed = plan.narrowed_to(&["greenshot".to_string()]);

        assert_eq!(narrowed.app_names(), vec!["Greenshot".to_string()]);
        assert!(narrowed.is_only_actions());
        let mixed = Plan { apps: vec![planned("Greenshot", Some("Install")), planned("Firefox", None)], os_update_available: false };
        assert!(!mixed.is_only_actions(), "an upgrade in the run brings the usual warning back");
    }

    #[test]
    fn the_summary_counts_updates_in_words_a_person_would_use() {
        assert_eq!(summary(1, 0), ("Updates installed".to_string(), "1 update was installed.".to_string()));
        assert_eq!(summary(3, 0).1, "3 updates were installed.");
    }

    /// Somebody who cannot read a log is never told to check one.
    #[test]
    fn a_failure_says_what_happens_next_rather_than_pointing_at_the_logs() {
        let (title, body) = summary(2, 1);

        assert_eq!(title, "1 update didn't install");
        assert!(body.starts_with("2 updates installed. "), "{body}");
        assert!(!body.to_lowercase().contains("log"), "{body}");
        assert_eq!(summary(0, 2).0, "2 updates didn't install");
    }

    #[test]
    fn a_forced_windows_update_warns_for_the_full_five_minutes_when_somebody_would_see_it() {
        assert_eq!(forced_os_warning(false), Duration::from_secs(300));
    }

    #[test]
    fn a_forced_windows_update_starts_at_once_when_nobody_would_see_the_warning() {
        assert_eq!(forced_os_warning(true), Duration::ZERO);
    }

    /// Finding: a locked screen used to mean "nobody there" and a 15-second restart that closed open
    /// work. Re-read right before restarting, a screen nobody is looking at means no restart.
    #[test]
    fn a_locked_or_dark_screen_right_before_the_restart_means_no_restart() {
        assert!(!should_ask_for_restart(Some("the workstation is locked")));
        assert!(!should_ask_for_restart(Some("the display is off")));
        assert!(should_ask_for_restart(None));
    }

    /// What the tray asks for is the full notice — and the service ignores it anyway.
    #[test]
    fn the_restart_the_tray_asks_for_is_never_shorter_than_the_warning() {
        assert_eq!(queue::RESTART_DELAY_ATTENDED, WARNING_PERIOD.as_secs());
        assert!(queue::restart_delay_in_bounds(queue::RESTART_DELAY_ATTENDED));
    }

    #[test]
    fn a_notice_left_up_for_the_whole_period_leaves_nothing_to_wait_out() {
        assert_eq!(remaining_warning(WARNING_PERIOD), Duration::ZERO);
        assert_eq!(remaining_warning(WARNING_PERIOD * 2), Duration::ZERO, "and an overrun is not negative time");
    }

    #[test]
    fn a_notice_read_and_dismissed_leaves_the_rest_of_the_period_to_run() {
        assert_eq!(remaining_warning(Duration::from_secs(60)), WARNING_PERIOD - Duration::from_secs(60));
    }

    /// The common unattended case, where the dialog gives up a moment either side of the period:
    /// what is left is rounding, not notice.
    #[test]
    fn a_remainder_of_a_few_seconds_is_no_remainder_at_all() {
        assert_eq!(remaining_warning(WARNING_PERIOD - Duration::from_secs(5)), Duration::ZERO);
    }

    #[test]
    fn clicking_patch_now_starts_immediately_rather_than_after_the_warning() {
        assert_eq!(Decision::from_choice(ConfirmChoice::PatchNow), Decision::PatchNow);
    }

    /// The dialog's own giveup *is* one delay period, so an unanswered dialog has already spent
    /// the time a delay buys: it counts down the budget and is re-asked at once, rather than
    /// postponing by a second period on top — which is what made the count fall once every two.
    #[test]
    fn an_unanswered_dialog_spends_a_delay_without_postponing_the_cycle_again() {
        assert_eq!(Decision::from_choice(ConfirmChoice::TimedOut), Decision::Unanswered);
    }

    #[test]
    fn clicking_delay_defers_the_cycle() {
        assert_eq!(Decision::from_choice(ConfirmChoice::Delay), Decision::Delayed);
    }
}
