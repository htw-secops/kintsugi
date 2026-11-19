use std::collections::BTreeMap;
use std::fs;
use std::path::{Path, PathBuf};

use serde::{Deserialize, Serialize};

/// How long to wait before retry 1, 2 and 3 of an application whose patch failed. After the third
/// retry fails the application is left alone until the next scheduled cycle (one policy interval)
/// tries it again from the start. Kept identical in the other two agents.
const BACKOFF_SECONDS: [u64; 3] = [15 * 60, 60 * 60, 4 * 60 * 60];

#[derive(Debug, Clone, Default, Serialize, Deserialize)]
struct Entry {
    retries_done: u32,
    /// `None` once the retries are used up: nothing more is due until a full cycle runs.
    next_attempt_epoch: Option<u64>,
}

/// Applications whose last patch failed, and when each is next worth trying — persisted in
/// `retry_state.json` beside `schedule.json` so a restart, a log-out or a night asleep does not lose
/// it, and compared against the wall clock for the same reason `ScheduleState` is.
///
/// It is **not** part of `ScheduleState`'s own file and does not touch its `delay_count`. A retry is
/// the agent finishing a job the person already agreed to, so it never asks again and never spends
/// the delay budget; the scheduled cycle's `register_completed` leaves this list alone too, which is
/// what lets a failure outlive the cycle that hit it. `ScheduleState` only *carries* the list, so
/// that it travels with the state into a cycle by ownership like everything else here (see
/// `main::spawn_cycle`). Kept identical in the other two agents.
#[derive(Debug, Clone, Default, Serialize, Deserialize)]
pub struct RetryList {
    entries: BTreeMap<String, Entry>,
    #[serde(skip)]
    path: PathBuf,
}

/// Applications are matched case-insensitively everywhere else in the agent, so the key is too.
fn key(application_name: &str) -> String {
    application_name.to_lowercase()
}

impl RetryList {
    /// What was saved, or an empty list: a missing or unreadable file means "nothing is waiting",
    /// which at worst costs a retry, never a patch.
    pub fn load(path: &Path) -> Self {
        let mut list = fs::read_to_string(path).ok().and_then(|text| serde_json::from_str::<Self>(&text).ok()).unwrap_or_default();
        list.path = path.to_path_buf();
        list
    }

    fn save(&self) {
        if let Some(parent) = self.path.parent() {
            let _ = fs::create_dir_all(parent);
        }
        if let Ok(json) = serde_json::to_string_pretty(self) {
            if let Err(err) = fs::write(&self.path, json) {
                crate::logging::warn(&format!("could not save the patch retry list to {}: {err:#}", self.path.display()));
            }
        }
    }

    /// Records a failed patch and schedules the next try.
    ///
    /// An attempt counts as a *retry* only if the entry was due: a failure on a first attempt, or
    /// in a full cycle that ran before the entry was due, starts the schedule again from 15 minutes.
    pub fn record_failure(&mut self, application_name: &str, now: u64) {
        let entry = self.entries.entry(key(application_name)).or_default();
        let was_a_retry = entry.next_attempt_epoch.is_some_and(|due| due <= now);
        entry.retries_done = if was_a_retry { entry.retries_done + 1 } else { 0 };
        entry.next_attempt_epoch = BACKOFF_SECONDS.get(entry.retries_done as usize).map(|wait| now + wait);

        match entry.next_attempt_epoch {
            Some(due) => crate::logging::info(&format!(
                "patch for {application_name} failed ({} of {} retries used); will retry at epoch {due}",
                entry.retries_done,
                BACKOFF_SECONDS.len()
            )),
            None => crate::logging::info(&format!(
                "patch for {application_name} failed and its {} retries are used up; waiting for the next scheduled cycle",
                BACKOFF_SECONDS.len()
            )),
        }
        self.save();
    }

    pub fn record_success(&mut self, application_name: &str) {
        if self.entries.remove(&key(application_name)).is_some() {
            self.save();
        }
    }

    /// The applications whose retry is due at `now`, for `patch_cycle::run_forced` to narrow to.
    pub fn due(&self, now: u64) -> Vec<String> {
        self.entries.iter().filter(|(_, entry)| entry.next_attempt_epoch.is_some_and(|due| due <= now)).map(|(name, _)| name.clone()).collect()
    }

    /// Pushes the entries for `names` back by the first wait, without using up a retry. For a run
    /// that could not even ask the server what to patch: that says nothing about the patch, and
    /// without it a due entry would ask again every minute for as long as the server was away.
    pub fn defer(&mut self, names: &[String], now: u64) {
        for name in names {
            if let Some(entry) = self.entries.get_mut(&key(name)).filter(|entry| entry.next_attempt_epoch.is_some()) {
                entry.next_attempt_epoch = Some(now + BACKOFF_SECONDS[0]);
            }
        }
        self.save();
    }

    /// Drops the entries for `names`: an application the server no longer lists as patchable (fixed
    /// by hand, removed, or its script unsigned) has nothing left to retry, and a due entry nothing
    /// ever clears would start an empty run every minute.
    pub fn forget(&mut self, names: &[String]) {
        let before = self.entries.len();
        for name in names {
            self.entries.remove(&key(name));
        }
        if self.entries.len() != before {
            self.save();
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn scratch(name: &str) -> RetryList {
        let dir = std::env::temp_dir().join(format!("kintsugi-retry-{name}-{}", std::process::id()));
        let _ = fs::remove_dir_all(&dir);
        RetryList::load(&dir.join("retry_state.json"))
    }

    fn due_at(list: &RetryList, name: &str) -> Option<u64> {
        list.entries.get(&key(name)).and_then(|entry| entry.next_attempt_epoch)
    }

    #[test]
    fn a_failure_is_retried_after_15_minutes_then_an_hour_then_four_and_then_left_alone() {
        let mut list = scratch("backoff");
        let mut now = 1_000;

        list.record_failure("Firefox", now);
        assert_eq!(due_at(&list, "Firefox"), Some(now + 15 * 60));

        for wait in [15 * 60, 60 * 60] {
            now += wait;
            assert_eq!(list.due(now), vec!["firefox".to_string()], "due once its wait is over");
            list.record_failure("Firefox", now);
        }
        assert_eq!(due_at(&list, "Firefox"), Some(now + 4 * 60 * 60));

        now += 4 * 60 * 60;
        list.record_failure("Firefox", now);
        assert_eq!(due_at(&list, "Firefox"), None, "three retries, then it waits for the next cycle");
        assert!(list.due(now + 1_000_000).is_empty());
    }

    #[test]
    fn nothing_is_due_before_its_time() {
        let mut list = scratch("not-yet");
        list.record_failure("Firefox", 1_000);

        assert!(list.due(1_000 + 15 * 60 - 1).is_empty());
    }

    #[test]
    fn a_success_clears_the_entry_and_a_later_failure_starts_over() {
        let mut list = scratch("success");
        list.record_failure("Firefox", 1_000);
        list.record_success("firefox");
        assert!(list.entries.is_empty(), "matched without regard to case");

        list.record_failure("Firefox", 9_000);
        assert_eq!(due_at(&list, "Firefox"), Some(9_000 + 15 * 60));
    }

    #[test]
    fn a_full_cycle_that_fails_again_after_the_retries_ran_out_starts_the_schedule_over() {
        let mut list = scratch("fresh-cycle");
        let mut now = 1_000;
        list.record_failure("Firefox", now);
        for wait in BACKOFF_SECONDS {
            now += wait;
            list.record_failure("Firefox", now);
        }
        assert_eq!(due_at(&list, "Firefox"), None);

        now += 24 * 60 * 60;
        list.record_failure("Firefox", now);

        assert_eq!(due_at(&list, "Firefox"), Some(now + 15 * 60), "a new interval is a new budget of retries");
    }

    #[test]
    fn a_failure_before_the_entry_was_due_is_not_counted_as_a_retry() {
        let mut list = scratch("early");
        list.record_failure("Firefox", 1_000);

        // A full cycle reaches it five minutes later, ten before the retry was due.
        list.record_failure("Firefox", 1_300);

        assert_eq!(due_at(&list, "Firefox"), Some(1_300 + 15 * 60));
    }

    #[test]
    fn the_list_survives_a_restart() {
        let mut list = scratch("persist");
        list.record_failure("Firefox", 1_000);

        let reloaded = RetryList::load(&list.path);

        assert_eq!(due_at(&reloaded, "Firefox"), Some(1_000 + 15 * 60));
        assert!(fs::read_to_string(&list.path).unwrap().contains("firefox"), "written to retry_state.json");
    }

    #[test]
    fn a_missing_or_corrupt_file_is_an_empty_list() {
        let list = scratch("corrupt");
        fs::create_dir_all(list.path.parent().unwrap()).unwrap();
        fs::write(&list.path, "not json").unwrap();

        assert!(RetryList::load(&list.path).entries.is_empty());
    }

    #[test]
    fn deferring_waits_again_without_using_up_a_retry() {
        let mut list = scratch("defer");
        list.record_failure("Firefox", 1_000);

        list.defer(&["Firefox".to_string()], 5_000);

        assert_eq!(due_at(&list, "Firefox"), Some(5_000 + 15 * 60));
        assert_eq!(list.entries["firefox"].retries_done, 0);
    }

    #[test]
    fn forgetting_one_application_leaves_the_others() {
        let mut list = scratch("forget");
        list.record_failure("Firefox", 1_000);
        list.record_failure("Gimp", 1_000);

        list.forget(&["GIMP".to_string()]);

        assert_eq!(list.entries.keys().collect::<Vec<_>>(), vec!["firefox"]);
    }
}
