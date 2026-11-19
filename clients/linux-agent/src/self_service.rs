//! The notification area's "Install Software" submenu: the titles an administrator has offered this host as
//! Self-service, cached where the per-user process can read them, and the snapshot that turns a
//! click back into a name.
//!
//! **The privileged half writes the cache and the per-user half only reads it.** The work list is
//! fetched with this host's identity, and it carries secret values on action rows; the per-user half
//! of the Windows and Linux agents holds no identity at all, and none of the three should hold a
//! secret it does not need. So `upgrade::act_on_assigned` writes just a name and a version per offer
//! — nothing a script needs and nothing secret — and a click sends the *name* back through the
//! existing `AppPatch` request, where the privileged half re-fetches the row and verifies every
//! signature again (`upgrade::find_runnable`). A forged request can therefore only ask for something
//! an administrator already offered this host. No new request kind, no new route.
//!
//! Kept identical in the other two agents, apart from where the file lives and how its mode is set.

use std::path::Path;
use std::sync::Mutex;

use anyhow::{Context, Result};
use serde::{Deserialize, Serialize};

use crate::upgrade::UpgradeStatus;

/// One title offered in the submenu. Deliberately not the `UpgradeStatus` it came from: no script,
/// no signature, and above all no secret ever reaches this file.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Offer {
    pub name: String,
    #[serde(default)]
    pub latest_version: Option<String>,
}

impl Offer {
    pub fn from_status(status: &UpgradeStatus) -> Self {
        Offer { name: status.application_name.clone(), latest_version: status.latest_version.clone() }
    }

    /// What the submenu item says — the name, and the version it would install when the server knows.
    pub fn label(&self) -> String {
        match self.latest_version.as_deref().filter(|version| !version.is_empty()) {
            Some(version) => format!("{} ({version})", self.name),
            None => self.name.clone(),
        }
    }
}

/// Writes the offers for the per-user half, replacing whatever was there.
///
/// Through a temporary file and a rename, so the per-user half never reads half a list. Mode `0644`
/// on the descriptor, so the umask cannot narrow it: the state directory is traverse-only (`0711`),
/// so the mode on the file is all there is — exactly as for `policy.json` and
/// `checkin-schedule.json` beside it. Only root can create or replace an entry there.
pub fn write(path: &Path, offers: &[Offer]) -> Result<()> {
    use std::io::Write;
    use std::os::unix::fs::{OpenOptionsExt, PermissionsExt};

    let json = serde_json::to_vec_pretty(offers).context("could not serialize the self-service offers")?;
    let staging = path.with_extension("json.tmp");
    let _ = std::fs::remove_file(&staging);
    let mut file = std::fs::OpenOptions::new()
        .write(true)
        .create_new(true)
        .mode(0o644)
        .custom_flags(libc::O_NOFOLLOW)
        .open(&staging)
        .with_context(|| format!("could not create {}", staging.display()))?;
    file.write_all(&json).context("could not write the self-service offers")?;
    file.set_permissions(std::fs::Permissions::from_mode(0o644)).context("could not set the self-service file's mode")?;
    drop(file);
    std::fs::rename(&staging, path).with_context(|| format!("could not move the self-service offers into {}", path.display()))
}

/// The offers as last written. Missing or unreadable reads as none, which hides the submenu — the
/// state of a host whose first timer check-in has not run yet, or that has nothing offered.
pub fn load(path: &Path) -> Vec<Offer> {
    std::fs::read(path)
        .ok()
        .and_then(|bytes| serde_json::from_slice(&bytes).ok())
        .unwrap_or_default()
}

/// The names the submenu was last built from, in item order.
///
/// `MenuAction::Install` carries an index rather than a name so it stays `Copy`, and an index is
/// only meaningful against the list it was drawn from — not against whatever the cache holds by
/// the time the click is handled, which the next check-in may have rewritten. So the menu records
/// what it drew, and the click is resolved against that.
static SHOWN: Mutex<Vec<String>> = Mutex::new(Vec::new());

pub fn remember_shown(offers: &[Offer]) {
    *SHOWN.lock().unwrap_or_else(|poisoned| poisoned.into_inner()) = offers.iter().map(|offer| offer.name.clone()).collect();
}

pub fn shown_name(index: usize) -> Option<String> {
    SHOWN.lock().unwrap_or_else(|poisoned| poisoned.into_inner()).get(index).cloned()
}

#[cfg(test)]
mod tests {
    use super::*;

    fn scratch(name: &str) -> std::path::PathBuf {
        let dir = std::env::temp_dir().join(format!("kintsugi-self-service-{name}-{}", std::process::id()));
        let _ = std::fs::remove_dir_all(&dir);
        std::fs::create_dir_all(&dir).unwrap();
        dir.join("self-service.json")
    }

    #[test]
    fn offers_round_trip_through_the_cache_readable_by_everyone() {
        use std::os::unix::fs::PermissionsExt;
        let path = scratch("round-trip");
        let offers = vec![
            Offer { name: "Greenshot".to_string(), latest_version: Some("1.3.300".to_string()) },
            Offer { name: "Firefox".to_string(), latest_version: None },
        ];

        write(&path, &offers).unwrap();

        assert_eq!(load(&path), offers);
        assert_eq!(std::fs::metadata(&path).unwrap().permissions().mode() & 0o777, 0o644);
    }

    /// The file is read by an unprivileged process and, on Windows, sits beside a directory
    /// ordinary users can write — so the one thing that must never be in it is a secret.
    #[test]
    fn the_cache_carries_a_name_and_a_version_and_nothing_else() {
        let json = serde_json::to_string(&Offer { name: "CrowdStrike".to_string(), latest_version: Some("7.1".to_string()) }).unwrap();

        assert_eq!(json, r#"{"name":"CrowdStrike","latestVersion":"7.1"}"#);
    }

    #[test]
    fn a_missing_or_garbled_cache_offers_nothing() {
        let path = scratch("missing");
        assert!(load(&path).is_empty());

        std::fs::write(&path, "not json").unwrap();
        assert!(load(&path).is_empty());
    }

    #[test]
    fn rewriting_replaces_the_list_rather_than_adding_to_it() {
        let path = scratch("rewrite");
        write(&path, &[Offer { name: "A".to_string(), latest_version: None }]).unwrap();

        write(&path, &[]).unwrap();

        assert!(load(&path).is_empty(), "a title withdrawn from self-service must leave the menu");
    }

    #[test]
    fn a_label_names_the_version_only_when_it_is_known() {
        assert_eq!(Offer { name: "Zoom".to_string(), latest_version: Some("6.2".to_string()) }.label(), "Zoom (6.2)");
        assert_eq!(Offer { name: "Zoom".to_string(), latest_version: Some(String::new()) }.label(), "Zoom");
        assert_eq!(Offer { name: "Zoom".to_string(), latest_version: None }.label(), "Zoom");
    }

    #[test]
    fn a_click_resolves_against_the_list_the_menu_drew() {
        remember_shown(&[Offer { name: "A".to_string(), latest_version: None }, Offer { name: "B".to_string(), latest_version: None }]);

        assert_eq!(shown_name(1).as_deref(), Some("B"));
        assert_eq!(shown_name(2), None, "an index past the list resolves to nothing, never to something else");
    }
}
