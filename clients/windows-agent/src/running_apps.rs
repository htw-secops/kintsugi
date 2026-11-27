//! Whether an application with a patch waiting is open right now — the one question that decides
//! whether patching it can go ahead without asking anybody.
//!
//! An upgrade replaces files a running program holds open, and most installers either refuse or
//! close the program to do it. Done to an application nobody has open, that costs the person at the
//! desk nothing, so the service patches those silently after each hourly check-in
//! (`service::Agent::patch_shut_applications_silently`) and the tray's due cycle patches them before
//! it prompts (`patch_cycle::run`); only the applications this module finds open are left for the
//! confirm/delay prompt.
//!
//! **Not knowing is answered as "open".** No uninstall entry to be found, an entry that names no
//! usable folder, a process table that could not be read: each leaves the row to the prompt, which
//! is exactly where every row went before this module existed. Quitting somebody's open
//! application with no warning is the one thing the feature must never do; a row prompted for
//! unnecessarily costs a dialog.
//!
//! What "open" means here: some process's image lives under a folder the application's uninstall
//! entry names — its `InstallLocation`, and the folder of its `DisplayIcon` executable, which is
//! nearly always the program itself. The entry is found by the row's `application_identifier`
//! (the uninstall key name, for a plain install) and otherwise by `DisplayName` (what a winget or
//! Chocolatey row is named by, since those identifiers are package ids rather than keys), across
//! both HKLM views and every loaded user hive — the service runs as SYSTEM, so `HKEY_CURRENT_USER`
//! is SYSTEM's own and holds nobody's per-user installs.
//!
//! A folder too broad to mean one application (`Program Files` itself, a drive root, anything under
//! `Windows`, a profile's `AppData\Local`) is not used: matched against, it would call nearly every
//! application open. Every process on the machine counts, not only one session's — the service sees
//! them all, and an application left open in a disconnected session is closed by its upgrade too.
//!
//! Nor is an installer's cache (`Package Cache`, `InstallShield Installation Information`), which
//! fails the other way. A WiX Burn bundle — python.org's installer, the .NET and Visual C++
//! runtimes, SSMS — sets no `InstallLocation` and points `DisplayIcon` at its cached setup `.exe`,
//! so that folder would be the only one found, nothing ever runs from it, and the application would
//! read as shut while it is open.
//!
//! Kept identical in intent in the other two agents; what each platform matches on differs and is
//! described in each one's copy. [`partition`] is the shared rule, and is the same in all three.

use crate::upgrade::UpgradeStatus;

/// Splits `apps` into those `is_open` says are shut — patchable now, without asking — and the
/// rest, in their original order. `None` from `is_open` ("could not tell") lands with the open
/// ones; see the module docs for why that is the rule the whole feature rests on. Kept identical
/// in the other two agents.
pub fn partition<T>(apps: Vec<T>, is_open: impl Fn(&T) -> Option<bool>) -> (Vec<T>, Vec<T>) {
    apps.into_iter().partition(|app| is_open(app) == Some(false))
}

/// `Some(true)`/`Some(false)` when it can say, `None` when it cannot. Reads the process table
/// afresh each time, because a silent pass can run for many minutes and somebody may open an
/// application between the list being split and its turn coming.
pub fn is_open(app: &UpgradeStatus) -> Option<bool> {
    let folders = application_folders(app.application_identifier.as_deref(), &app.application_name);
    if folders.is_empty() {
        return None;
    }
    match running_images() {
        Ok(images) => Some(images.iter().any(|image| folders.iter().any(|folder| is_under(image, folder)))),
        Err(err) => {
            crate::logging::warn(&format!("could not list running processes, so {} is treated as open: {err:#}", app.application_name));
            None
        }
    }
}

/// `path` lower-cased, with forward slashes turned back and quotes and a trailing separator gone,
/// so two spellings of one folder compare equal.
fn normalize(path: &str) -> String {
    path.trim().trim_matches('"').replace('/', "\\").trim_end_matches('\\').to_lowercase()
}

/// Whether `image` lives somewhere under `folder`; both already [`normalize`]d.
fn is_under(image: &str, folder: &str) -> bool {
    image.strip_prefix(folder).is_some_and(|rest| rest.starts_with('\\'))
}

/// The executable a `DisplayIcon` names, without its `,<index>` suffix or quotes — or `None` when it
/// is not an `.exe` (an `.ico`, a `.dll` resource) and so says nothing about what runs.
fn icon_executable(display_icon: &str) -> Option<String> {
    let trimmed = display_icon.trim().trim_matches('"');
    let without_index = match trimmed.rsplit_once(',') {
        Some((path, index)) if index.trim().trim_start_matches('-').chars().all(|c| c.is_ascii_digit()) => path,
        _ => trimmed,
    };
    let path = normalize(without_index);
    path.ends_with(".exe").then_some(path)
}

/// Whether a normalized folder is narrow enough to stand for one application — see the module docs.
fn is_specific(folder: &str) -> bool {
    let parts: Vec<&str> = folder.split('\\').filter(|part| !part.is_empty()).collect();
    let Some((drive, rest)) = parts.split_first() else { return false };
    if !drive.ends_with(':') || rest.is_empty() {
        return false;
    }
    let broad = |names: &[&str]| rest.len() == names.len() && rest.iter().zip(names).all(|(a, b)| a == b);
    if rest[0] == "windows" {
        return false;
    }
    if rest.iter().any(|part| *part == "package cache" || *part == "installshield installation information") {
        return false;
    }
    if broad(&["program files"]) || broad(&["program files (x86)"]) || broad(&["programdata"]) || broad(&["users"]) {
        return false;
    }
    // A profile, and the per-user folders under it that installers put many applications in.
    if rest[0] == "users" {
        let below_profile = &rest[1..];
        let is = |names: &[&str]| below_profile.len() == names.len() + 1 && below_profile[1..].iter().zip(names).all(|(a, b)| a == b);
        if below_profile.len() == 1
            || is(&["appdata"])
            || is(&["appdata", "local"])
            || is(&["appdata", "roaming"])
            || is(&["appdata", "local", "programs"])
        {
            return false;
        }
    }
    true
}

/// The folders one uninstall entry says the application runs from — its `InstallLocation` and its
/// `DisplayIcon` executable's folder — keeping only the specific ones.
fn folders_from_entry(install_location: Option<&str>, display_icon: Option<&str>) -> Vec<String> {
    let mut folders = Vec::new();
    if let Some(location) = install_location.map(normalize).filter(|location| !location.is_empty()) {
        folders.push(location);
    }
    if let Some(folder) = display_icon.and_then(icon_executable).and_then(|exe| exe.rsplit_once('\\').map(|(folder, _)| folder.to_string())) {
        folders.push(folder);
    }
    folders.retain(|folder| is_specific(folder));
    folders.dedup();
    folders
}

fn application_folders(identifier: Option<&str>, name: &str) -> Vec<String> {
    use winreg::enums::{HKEY_LOCAL_MACHINE, HKEY_USERS, KEY_READ, KEY_WOW64_32KEY, KEY_WOW64_64KEY};
    use winreg::RegKey;

    const UNINSTALL_KEY: &str = r"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    let mut views: Vec<RegKey> = [KEY_WOW64_64KEY, KEY_WOW64_32KEY]
        .into_iter()
        .filter_map(|view| RegKey::predef(HKEY_LOCAL_MACHINE).open_subkey_with_flags(UNINSTALL_KEY, KEY_READ | view).ok())
        .collect();
    let users = RegKey::predef(HKEY_USERS);
    for sid in users.enum_keys().flatten().filter(|sid| sid.starts_with("S-1-5-21-") && !sid.ends_with("_Classes")) {
        if let Ok(key) = users.open_subkey_with_flags(format!(r"{sid}\{UNINSTALL_KEY}"), KEY_READ) {
            views.push(key);
        }
    }

    // A folder that is not on disk any more is dropped rather than matched: an entry left pointing
    // at an old location would otherwise read as shut while the application runs from a new one.
    let read = |entry: &RegKey| {
        let location: Option<String> = entry.get_value("InstallLocation").ok();
        let icon: Option<String> = entry.get_value("DisplayIcon").ok();
        let mut folders = folders_from_entry(location.as_deref(), icon.as_deref());
        folders.retain(|folder| std::path::Path::new(folder).is_dir());
        folders
    };

    // By key name first: it is exact, where a display name can be shared by two installs.
    if let Some(identifier) = identifier {
        let folders: Vec<String> =
            views.iter().filter_map(|view| view.open_subkey_with_flags(identifier, KEY_READ).ok()).flat_map(|entry| read(&entry)).collect();
        if !folders.is_empty() {
            return folders;
        }
    }

    views
        .iter()
        .flat_map(|view| view.enum_keys().flatten().filter_map(move |key| view.open_subkey_with_flags(&key, KEY_READ).ok()))
        .filter(|entry| entry.get_value::<String, _>("DisplayName").is_ok_and(|display| display.trim().eq_ignore_ascii_case(name.trim())))
        .flat_map(|entry| read(&entry))
        .collect()
}

/// Every running process's image path, [`normalize`]d. A process the service may not open (a
/// protected one) is simply absent; only the listing itself failing is an error.
fn running_images() -> anyhow::Result<Vec<String>> {
    use windows_sys::Win32::Foundation::CloseHandle;
    use windows_sys::Win32::System::ProcessStatus::K32EnumProcesses;
    use windows_sys::Win32::System::Threading::{OpenProcess, QueryFullProcessImageNameW, PROCESS_NAME_WIN32, PROCESS_QUERY_LIMITED_INFORMATION};

    let mut pids = vec![0u32; 4096];
    loop {
        let mut needed = 0u32;
        let bytes = (pids.len() * std::mem::size_of::<u32>()) as u32;
        // SAFETY: the buffer is `bytes` long and lives across the call.
        if unsafe { K32EnumProcesses(pids.as_mut_ptr(), bytes, &mut needed) } == 0 {
            anyhow::bail!("EnumProcesses failed: {}", std::io::Error::last_os_error());
        }
        // A full buffer may have been truncated; try again with room to spare.
        if needed < bytes {
            pids.truncate(needed as usize / std::mem::size_of::<u32>());
            break;
        }
        pids.resize(pids.len() * 2, 0);
    }

    let mut images = Vec::with_capacity(pids.len());
    let mut buffer = vec![0u16; 32_768];
    for pid in pids.into_iter().filter(|pid| *pid != 0) {
        // SAFETY: plain call; a null handle is checked below.
        let process = unsafe { OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, 0, pid) };
        if process.is_null() {
            continue;
        }
        let mut length = buffer.len() as u32;
        // SAFETY: `buffer` holds `length` UTF-16 units and lives across the call.
        let ok = unsafe { QueryFullProcessImageNameW(process, PROCESS_NAME_WIN32, buffer.as_mut_ptr(), &mut length) };
        // SAFETY: `process` is a handle this function opened.
        unsafe { CloseHandle(process) };
        if ok != 0 {
            images.push(normalize(&String::from_utf16_lossy(&buffer[..length as usize])));
        }
    }
    Ok(images)
}

/// What the silent pass has already tried, so a row it cannot patch is not retried every hour.
///
/// The silent pass runs hourly; the prompted cycle runs once per patching interval. Without this, an
/// upgrade that fails — or a row the server keeps counting behind while its script correctly finds
/// nothing to do — would be retried, re-reported and (for anything an upgrade quits before failing)
/// re-quit every hour rather than once per cycle. So each `(application, target version)` the pass
/// attempts is written down with the signature of what it ran and when, and later passes skip it.
/// An entry whose row is no longer pending is dropped.
///
/// A skipped row is tried again by the pass itself, with nobody needing to be there, on any of three
/// things. A new target version. A different signature: somebody has signed a fix for the script
/// that failed, which used to reach an unattended machine only once a new version shipped, because
/// on Windows and macOS the due cycle needs somebody logged in. Or [`SILENT_RETRY_AFTER_SECS`] since
/// the last attempt, for a failure that was only ever transient (an installer busy, a download
/// dropped). An entry recorded with no signature — the OS passes' `silent-os-attempts.json`, where
/// nothing is signed — never expires, and is tried once per revision or label as before. Kept
/// identical in the other two agents.
#[derive(Debug, Default, PartialEq, Eq, serde::Serialize, serde::Deserialize)]
pub struct SilentAttempts {
    attempted: Vec<(String, Option<String>)>,
    /// The signature each entry of `attempted` ran with, index for index. Beside the tuple rather
    /// than in it, because serde reads a tuple at a fixed length and every file written before this
    /// existed would then fail to load; `load` pads a file that has none.
    #[serde(default)]
    signatures: Vec<Option<String>>,
    /// When each entry of `attempted` was tried, in epoch seconds, index for index.
    #[serde(default)]
    tried_at: Vec<u64>,
}

/// How long the silent pass leaves a signed row it has tried before trying it again at the same
/// target and signature: a day, so a row that keeps failing is re-reported daily rather than hourly.
pub const SILENT_RETRY_AFTER_SECS: u64 = 24 * 60 * 60;

impl SilentAttempts {
    /// What is on disk, or nothing tried yet when the file is missing or unreadable — which costs at
    /// most one extra attempt per row. A file from before signatures were kept reads as tried with
    /// none, at epoch 0: an application row, which now comes with a signature, is tried once more.
    pub fn load(path: &std::path::Path) -> Self {
        let mut attempts: Self =
            std::fs::read_to_string(path).ok().and_then(|text| serde_json::from_str(&text).ok()).unwrap_or_default();
        attempts.signatures.resize(attempts.attempted.len(), None);
        attempts.tried_at.resize(attempts.attempted.len(), 0);
        attempts
    }

    pub fn save(&self, path: &std::path::Path) {
        let written = serde_json::to_string(self).map_err(anyhow::Error::from).and_then(|text| Ok(std::fs::write(path, text)?));
        if let Err(err) = written {
            crate::logging::warn(&format!("could not record the silent pass's attempts at {}: {err:#}", path.display()));
        }
    }

    /// Forgets every attempt whose `(name, version)` is not among `pending` — patched since, or
    /// moved on to a newer target.
    pub fn retain_pending<'a>(&mut self, pending: impl IntoIterator<Item = (&'a str, Option<&'a str>)>) {
        let pending: Vec<(&str, Option<&str>)> = pending.into_iter().collect();
        let old = std::mem::take(self);
        for ((entry, signature), tried_at) in old.attempted.into_iter().zip(old.signatures).zip(old.tried_at) {
            if pending.iter().any(|(n, v)| n.eq_ignore_ascii_case(&entry.0) && *v == entry.1.as_deref()) {
                self.attempted.push(entry);
                self.signatures.push(signature);
                self.tried_at.push(tried_at);
            }
        }
    }

    /// Whether the pass leaves this row alone: tried at this target with this signature, and — when
    /// there is a signature — less than [`SILENT_RETRY_AFTER_SECS`] before `now`.
    pub fn already_tried(&self, name: &str, version: Option<&str>, signature: Option<&str>, now: u64) -> bool {
        self.position(name, version).is_some_and(|i| {
            self.signatures[i].as_deref() == signature
                && (signature.is_none() || now.saturating_sub(self.tried_at[i]) < SILENT_RETRY_AFTER_SECS)
        })
    }

    /// Written down at `now`, replacing an earlier attempt at the same target.
    pub fn record(&mut self, name: &str, version: Option<&str>, signature: Option<&str>, now: u64) {
        match self.position(name, version) {
            Some(i) => {
                self.signatures[i] = signature.map(str::to_string);
                self.tried_at[i] = now;
            }
            None => {
                self.attempted.push((name.to_string(), version.map(str::to_string)));
                self.signatures.push(signature.map(str::to_string));
                self.tried_at.push(now);
            }
        }
    }

    fn position(&self, name: &str, version: Option<&str>) -> Option<usize> {
        self.attempted.iter().position(|(n, v)| n.eq_ignore_ascii_case(name) && v.as_deref() == version)
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn an_image_under_the_install_folder_is_the_application_open() {
        let folder = normalize(r"C:\Program Files\Mozilla Firefox\");
        assert!(is_under(&normalize(r"C:\PROGRAM FILES\Mozilla Firefox\firefox.exe"), &folder));
        assert!(!is_under(&normalize(r"C:\Program Files\Mozilla Firefox Beta\firefox.exe"), &folder), "a sibling sharing a prefix is not inside it");
    }

    #[test]
    fn a_display_icon_names_its_executable_without_the_index_or_quotes() {
        assert_eq!(icon_executable(r#""C:\Program Files\Notepad++\notepad++.exe",0"#).as_deref(), Some(r"c:\program files\notepad++\notepad++.exe"));
        assert_eq!(icon_executable(r"C:\Program Files\7-Zip\7zFM.exe,-1").as_deref(), Some(r"c:\program files\7-zip\7zfm.exe"));
        assert_eq!(icon_executable(r"C:\Program Files\App\app.ico"), None, "an icon file says nothing about what runs");
    }

    #[test]
    fn a_folder_too_broad_to_mean_one_application_is_not_used() {
        for broad in [r"c:", r"c:\program files", r"c:\program files (x86)", r"c:\windows\system32", r"c:\programdata", r"c:\users\alice", r"c:\users\alice\appdata\local", r"c:\users\alice\appdata\local\programs"] {
            assert!(!is_specific(broad), "{broad} should be refused");
        }
        for specific in [r"c:\program files\mozilla firefox", r"c:\python313", r"c:\users\alice\appdata\local\programs\microsoft vs code"] {
            assert!(is_specific(specific), "{specific} should be accepted");
        }
    }

    #[test]
    fn an_entry_contributes_its_location_and_its_icon_folder() {
        assert_eq!(
            folders_from_entry(Some(r"C:\Program Files\VideoLAN\VLC\"), Some(r"C:\Program Files\VideoLAN\VLC\vlc.exe,0")),
            [r"c:\program files\videolan\vlc"]
        );
        // An MSI install commonly has no InstallLocation and a DisplayIcon under Windows\Installer.
        assert!(folders_from_entry(None, Some(r"C:\Windows\Installer\{GUID}\icon.exe")).is_empty());
        // A Burn bundle's DisplayIcon is its cached setup, which is never where the application runs.
        assert!(folders_from_entry(None, Some(r"C:\ProgramData\Package Cache\{GUID}\python-3.13.0-amd64.exe,0")).is_empty());
        assert!(folders_from_entry(None, Some(r"C:\Users\alice\AppData\Local\Package Cache\{GUID}\setup.exe,0")).is_empty());
        assert!(folders_from_entry(None, Some(r"C:\Program Files (x86)\InstallShield Installation Information\{GUID}\setup.exe")).is_empty());
    }

    #[test]
    fn an_attempt_is_skipped_until_its_target_moves_on_or_it_stops_being_pending() {
        let mut attempts = SilentAttempts::default();
        attempts.record("Firefox", Some("2.0"), Some("sig-a"), 1_000);
        attempts.record("GIMP", None, Some("sig-b"), 1_000);
        assert!(attempts.already_tried("firefox", Some("2.0"), Some("sig-a"), 1_000));
        assert!(!attempts.already_tried("Firefox", Some("3.0"), Some("sig-a"), 1_000), "a new target version is a new attempt");

        attempts.retain_pending([("Firefox", Some("3.0")), ("GIMP", None)]);
        assert!(!attempts.already_tried("Firefox", Some("2.0"), Some("sig-a"), 1_000), "the old target is forgotten");
        assert!(attempts.already_tried("GIMP", None, Some("sig-b"), 1_000), "still pending, still skipped");

        attempts.retain_pending(std::iter::empty());
        assert_eq!(attempts, SilentAttempts::default(), "nothing pending, nothing remembered");
    }

    #[test]
    fn an_attempt_is_tried_again_once_a_fix_is_signed_or_a_day_has_passed() {
        let mut attempts = SilentAttempts::default();
        attempts.record("Firefox", Some("2.0"), Some("sig-a"), 1_000);
        assert!(!attempts.already_tried("Firefox", Some("2.0"), Some("sig-fixed"), 1_000), "a newly signed fix is a new attempt");

        let a_day_later = 1_000 + SILENT_RETRY_AFTER_SECS;
        assert!(attempts.already_tried("Firefox", Some("2.0"), Some("sig-a"), a_day_later - 1));
        assert!(!attempts.already_tried("Firefox", Some("2.0"), Some("sig-a"), a_day_later), "a day on, a transient failure is tried again");

        attempts.record("Firefox", Some("2.0"), Some("sig-fixed"), a_day_later);
        assert!(attempts.already_tried("Firefox", Some("2.0"), Some("sig-fixed"), a_day_later), "the new attempt replaces the old");
        assert!(!attempts.already_tried("Firefox", Some("2.0"), Some("sig-a"), a_day_later));

        // An OS update has nothing signed, and keeps its once-per-revision rule however long ago it was.
        attempts.record("Security Update", Some("200"), None, 1_000);
        assert!(attempts.already_tried("Security Update", Some("200"), None, 1_000 + 10 * SILENT_RETRY_AFTER_SECS));
    }

    #[test]
    fn a_file_written_before_signatures_were_kept_still_loads() {
        let path = std::env::temp_dir().join(format!("kintsugi-silent-attempts-test-{}.json", std::process::id()));
        std::fs::write(&path, r#"{"attempted":[["Firefox","2.0"],["Security Update",null]]}"#).unwrap();
        let mut attempts = SilentAttempts::load(&path);

        assert!(!attempts.already_tried("Firefox", Some("2.0"), Some("sig-a"), 1_000), "an application is tried once more, now with its signature");
        assert!(attempts.already_tried("Security Update", None, None, 1_000), "an OS update is still tried once");

        attempts.record("Firefox", Some("2.0"), Some("sig-a"), 1_000);
        attempts.retain_pending([("Firefox", Some("2.0")), ("Security Update", None)]);
        attempts.save(&path);
        let reloaded = SilentAttempts::load(&path);
        let _ = std::fs::remove_file(&path);
        assert_eq!(reloaded, attempts);
        assert!(reloaded.already_tried("Firefox", Some("2.0"), Some("sig-a"), 1_000));
        assert!(reloaded.already_tried("Security Update", None, None, 1_000));
    }

    #[test]
    fn partition_sends_could_not_tell_to_the_prompt() {
        let (shut, rest) = partition(vec![("Shut", Some(false)), ("Open", Some(true)), ("Unknown", None)], |app| app.1);
        assert_eq!(shut.iter().map(|app| app.0).collect::<Vec<_>>(), ["Shut"]);
        assert_eq!(rest.iter().map(|app| app.0).collect::<Vec<_>>(), ["Open", "Unknown"]);
    }
}
