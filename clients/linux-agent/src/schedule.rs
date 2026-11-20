use std::fs;
use std::path::{Path, PathBuf};
use std::time::{SystemTime, UNIX_EPOCH};

use serde::{Deserialize, Serialize};

use crate::policy::PatchingPolicy;
use crate::retry_state::RetryList;

/// Wall-clock seconds since the epoch. Public because `patch_cycle` needs to stamp when a
/// confirmation dialog went up in the same clock this state is scheduled against — see
/// `register_unanswered_prompt`, and note that `Instant` would be the wrong tool: it stops during
/// system sleep, which is exactly the case that has to be counted.
pub fn now_epoch() -> u64 {
    SystemTime::now().duration_since(UNIX_EPOCH).unwrap_or_default().as_secs()
}

fn retry_path(schedule_path: &Path) -> PathBuf {
    schedule_path.with_file_name("retry_state.json")
}

/// Persisted locally (per logged-in user — see `config::user_state_dir`) so scheduling survives
/// an agent restart, a log-out/log-in cycle, and — since this is just plain wall-clock
/// comparison, not a running timer — system sleep: the next check after waking naturally sees
/// however much real time actually passed, no separate wake-detection needed.
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct ScheduleState {
    next_due_epoch: u64,
    delay_count: u32,
    #[serde(skip_serializing_if = "Option::is_none")]
    last_completed_epoch: Option<u64>,
    /// Failed patches waiting for a retry, in `retry_state.json` beside this file. Carried here only
    /// so it travels with the state into a cycle (see `main::spawn_cycle`); it is its own file, and
    /// nothing below — `register_completed` included — resets it or reads `delay_count` for it.
    #[serde(skip)]
    retries: RetryList,
    #[serde(skip)]
    path: PathBuf,
}

impl ScheduleState {
    /// Loads previously persisted state, or starts fresh — a brand-new install isn't due
    /// immediately on first run, only after one full interval, so installing the agent doesn't
    /// itself trigger an immediate confirmation prompt.
    pub fn load_or_default(path: &Path, policy: &PatchingPolicy) -> Self {
        if let Some(mut state) = fs::read_to_string(path).ok().and_then(|c| serde_json::from_str::<Self>(&c).ok()) {
            state.path = path.to_path_buf();
            state.retries = RetryList::load(&retry_path(path));
            return state;
        }

        let state = Self {
            next_due_epoch: now_epoch() + policy.interval_seconds(),
            delay_count: 0,
            last_completed_epoch: None,
            retries: RetryList::load(&retry_path(path)),
            path: path.to_path_buf(),
        };
        state.save();
        state
    }

    pub fn retries_mut(&mut self) -> &mut RetryList {
        &mut self.retries
    }

    /// The applications whose retry is due now. A scheduler starts a retry run for them when the
    /// scheduled cycle is not itself due — see `patch_cycle::run_forced`, which is what runs it.
    ///
    /// None while the person has delayed the cycle (`delay_count` is only above zero between a
    /// Delay and the cycle that finally runs): `register_delay` makes the cycle not due, which is
    /// exactly when a scheduler starts retries, so without this a retry would close the app inside
    /// the window that was just asked for. The delayed cycle includes the app anyway.
    pub fn due_retries(&self) -> Vec<String> {
        if self.delay_count > 0 {
            return Vec::new();
        }
        self.retries.due(now_epoch())
    }

    #[cfg(test)]
    pub fn retry_file_for_test(&self) -> PathBuf {
        retry_path(&self.path)
    }

    fn save(&self) {
        if let Some(parent) = self.path.parent() {
            let _ = fs::create_dir_all(parent);
        }
        if let Ok(json) = serde_json::to_string_pretty(self) {
            if let Err(err) = fs::write(&self.path, json) {
                crate::logging::warn(&format!("could not save patching schedule state to {}: {err}", self.path.display()));
            }
        }
    }

    pub fn is_due(&self) -> bool {
        now_epoch() >= self.next_due_epoch
    }

    pub fn next_due_epoch(&self) -> u64 {
        self.next_due_epoch
    }

    pub fn can_delay(&self, policy: &PatchingPolicy) -> bool {
        self.delay_count < policy.max_delay_count
    }

    pub fn delays_remaining(&self, policy: &PatchingPolicy) -> u32 {
        policy.max_delay_count.saturating_sub(self.delay_count)
    }

    /// Postpones this due cycle by one delay period, without resetting `delay_count` — the same
    /// prompt (and its shrinking delay budget) resumes once the delay elapses, rather than
    /// starting a fresh set of delays.
    pub fn register_delay(&mut self, policy: &PatchingPolicy) {
        self.next_due_epoch = now_epoch() + policy.delay_seconds();
        self.delay_count += 1;
        self.save();
        crate::logging::info(&format!(
            "patching delayed by {} ({} of {} delays used); next due at epoch {}",
            policy.delay_label(),
            self.delay_count,
            policy.max_delay_count,
            self.next_due_epoch
        ));
    }

    /// Records a confirmation dialog that went unanswered until it gave up, which still counts
    /// against the delay budget — the user was asked and said nothing — but must not postpone the
    /// cycle the way `register_delay` does.
    ///
    /// The dialog's own giveup *is* one delay period (`patch_cycle` passes `delay_seconds` as the
    /// timeout), so by the time it fires, the time a delay buys has already been spent waiting for
    /// an answer. Postponing by another period on top of that made the budget count down once
    /// every *two* periods — eight one-hour delays took sixteen hours. So the cycle is left due
    /// immediately and the next poll tick re-asks with the count decremented, which is what makes
    /// an ignored dialog fall 8 → 7 → 6 once an hour.
    ///
    /// `shown_at_epoch` is when the dialog went up, and the count credits however many whole delay
    /// periods actually elapsed rather than assuming one: a machine that slept for hours with the
    /// dialog open wakes to have `dialogs` take the prompt down within seconds — the deadline is
    /// held on the wall clock by this process, because the dialog program's own timer stops while
    /// the machine sleeps — and the delays that passed while it was asleep are spent here rather
    /// than started again. Never fewer than one, since the dialog only gives up after a full
    /// period.
    pub fn register_unanswered_prompt(&mut self, policy: &PatchingPolicy, shown_at_epoch: u64) {
        let period = policy.delay_seconds().max(1);
        let elapsed = now_epoch().saturating_sub(shown_at_epoch);
        let periods = u32::try_from(elapsed / period).unwrap_or(u32::MAX).max(1);

        self.next_due_epoch = now_epoch();
        self.delay_count = self.delay_count.saturating_add(periods).min(policy.max_delay_count);
        self.save();
        crate::logging::info(&format!(
            "patch confirmation went unanswered for {periods} delay period(s) ({} of {} delays used); due again now",
            self.delay_count, policy.max_delay_count
        ));
    }

    /// Records a completed (or abandoned/failed — see the caller) patch cycle: resets the delay
    /// budget and schedules the next cycle a full interval out.
    pub fn register_completed(&mut self, policy: &PatchingPolicy) {
        let now = now_epoch();
        self.last_completed_epoch = Some(now);
        self.next_due_epoch = now + policy.interval_seconds();
        self.delay_count = 0;
        self.save();
        crate::logging::info(&format!("patch cycle completed; next due at epoch {}", self.next_due_epoch));
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    /// One hour of delay, eight of them — the shape the countdown has to hold to: an ignored
    /// dialog spends one an hour, not one every two hours.
    fn policy() -> PatchingPolicy {
        PatchingPolicy::for_test(1, 1, 8)
    }

    fn scratch_state(name: &str, policy: &PatchingPolicy) -> ScheduleState {
        let path = std::env::temp_dir().join(format!("kintsugi-schedule-{name}-{}.json", std::process::id()));
        let _ = fs::remove_file(&path);
        ScheduleState::load_or_default(&path, policy)
    }

    #[test]
    fn no_retry_is_due_while_the_person_has_delayed_the_cycle() {
        let policy = policy();
        // Its own directory: `scratch_state` would share one temp-dir `retry_state.json` with every other test.
        let dir = std::env::temp_dir().join(format!("kintsugi-schedule-retry-held-{}", std::process::id()));
        let _ = fs::remove_dir_all(&dir);
        let mut state = ScheduleState::load_or_default(&dir.join("schedule.json"), &policy);
        state.retries_mut().record_failure("Firefox", 1);
        assert_eq!(state.due_retries(), vec!["firefox".to_string()]);

        state.register_delay(&policy);
        assert!(state.due_retries().is_empty(), "not inside the delay just asked for");

        state.register_completed(&policy);
        assert_eq!(state.due_retries(), vec!["firefox".to_string()], "and due again once the cycle has run");
    }

    #[test]
    fn an_unanswered_prompt_spends_one_delay_and_leaves_the_cycle_due_at_once() {
        let policy = policy();
        let mut state = scratch_state("unanswered-once", &policy);

        // The dialog's giveup is one delay period, so this is a dialog that has just given up.
        state.register_unanswered_prompt(&policy, now_epoch() - policy.delay_seconds());

        assert_eq!(state.delays_remaining(&policy), 7, "the budget counts down, as if Delay had been clicked");
        assert!(state.is_due(), "but the period it bought has been spent already, so the next tick re-asks");
    }

    /// The sleep case, and the reason this credits elapsed time rather than assuming one period:
    /// a machine that slept with the dialog open wakes to a giveup that fires immediately, and
    /// the delays that passed while it was asleep have to be spent rather than started again.
    #[test]
    fn a_prompt_that_stood_through_several_periods_spends_all_of_them() {
        let policy = policy();
        let mut state = scratch_state("unanswered-slept", &policy);

        state.register_unanswered_prompt(&policy, now_epoch() - 3 * policy.delay_seconds());

        assert_eq!(state.delays_remaining(&policy), 5);
        assert!(state.can_delay(&policy), "three of eight is not the whole budget");
    }

    #[test]
    fn a_prompt_left_up_past_the_whole_budget_spends_it_exactly_once_over() {
        let policy = policy();
        let mut state = scratch_state("unanswered-overrun", &policy);

        state.register_unanswered_prompt(&policy, now_epoch() - 100 * policy.delay_seconds());

        assert_eq!(state.delays_remaining(&policy), 0);
        assert!(!state.can_delay(&policy), "the next tick has to reach the no-delays-left branch");
    }
}

#[cfg(test)]
impl ScheduleState {
    /// Brings the next due time forward to now, so a test doesn't have to wait out an interval to
    /// exercise what happens once a cycle is due.
    pub fn force_due_for_test(&mut self) {
        self.next_due_epoch = 0;
        self.save();
    }
}
