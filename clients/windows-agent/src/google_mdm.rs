//! A `google-mdm` baseline item: is this PC's Google Windows device management (MDM) enrolment still
//! alive? In `enforce` mode, a dead one is unregistered so that the Google Credential Provider for
//! Windows (GCPW) enrols the PC again, as whoever next signs in with Google.
//!
//! **Why this exists.** GCPW enrols a PC once, as the first person to sign in with Google. If that
//! account is later deleted or suspended, Google refuses every sync from the enrolment, so the PC
//! never receives another Google Windows setting. GCPW still never re-enrols it, because its check
//! (`IsEnrolledWithGoogleMdm` in Chromium's `chrome/credential_provider/gaiacp/mdm_utils.cc`) only
//! asks Windows whether the PC is registered at Google's address, and the dead enrolment still says
//! yes. Once that one enrolment is unregistered, GCPW asks for a Google sign-in when the sign-in or
//! lock screen next starts (`NOT_ENROLLED_WITH_MDM`), and that sign-in enrols the PC again. Seen end to
//! end on one PC whose enrolment had failed every sync for a year: unregistered, re-enrolled at the
//! next Google sign-in, first successful sync a minute later.
//!
//! **What is read**, as SYSTEM, read-only:
//! - `HKLM\SOFTWARE\Microsoft\Enrollments\<id>`: `ProviderID`, `UPN`, `EnrollmentState` (1 is live,
//!   4 is an enrolment that was unregistered), `DiscoveryServiceFullURL`.
//! - `HKLM\SOFTWARE\Microsoft\Provisioning\OMADM\Accounts\<id>\Protected\ConnInfo`:
//!   `ServerLastSuccessTime` and `ServerLastFailureTime`. They end in `Z` but are device-local time;
//!   only their dates are used, so the offset does not matter.
//! - `HKLM\SOFTWARE\Google\GCPW` (is GCPW here?) and `...\GCPW\Users\<sid>`: `email`, which local
//!   accounts GCPW has associated with which Google account.
//! - `HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList\<sid>`: when each of those
//!   profiles was last loaded.
//! - `GetDeviceRegistrationInfo` (`mdmregistration.dll`): what GCPW itself sees.
//!
//! **The one change**, made only by `judge` in `enforce` mode when every guard in `plan_action`
//! holds: `UnregisterDeviceWithManagement(<id>)` for the live Google enrolment. Windows rolls back the
//! policies that enrolment set. Nothing else is created, set or deleted here: no other enrolment, no
//! GCPW association, no profile.
//!
//! **Until the next Google sign-in the PC has no Google Windows settings.** GCPW decides whether to ask
//! for a Google sign-in when the sign-in or lock screen *starts*, so an enrolment unregistered while a
//! person is working is picked up at their next lock, lid-close or restart. One unregistered while the
//! lock screen is already up is picked up at the one after.

use time::{Date, Month, OffsetDateTime};

use crate::baseline::{ItemResult, COMPLIANT, DRIFTED, ERROR, MAX_DETAIL_CHARS, MAX_OBSERVED_CHARS, NOT_APPLICABLE};

/// `BaselineItemRules.KindGoogleMdm`.
pub(crate) const KIND: &str = "google-mdm";

/// The host in the discovery URL GCPW enrols with (`kDefaultMdmUrl` in Chromium's gaiacp).
const GOOGLE_DISCOVERY_HOST: &str = "deviceenrollmentforwindows.googleapis.com";

/// `BaselineItemRules.GoogleMdmMinStaleDays` / `GoogleMdmMaxStaleDays`: an item outside this range is
/// refused here too.
const MIN_STALE_DAYS: i64 = 3;
const MAX_STALE_DAYS: i64 = 90;

/// A failure this recent means Windows is still trying and Google is refusing. With no recent attempt
/// the PC may just have been offline, which is not a reason to unregister anything.
const RECENT_FAILURE_DAYS: i64 = 2;

/// Someone other than the enrolling account must have used this PC this recently, or nobody would
/// enrol it again: a leaver's PC waiting to come back is left alone.
const OTHER_USER_RECENT_DAYS: i64 = 14;

const ENROLLMENT_LIVE: u32 = 1;

/// One Google enrolment under `HKLM\SOFTWARE\Microsoft\Enrollments`, in any state.
#[derive(Debug, Clone, PartialEq, Eq)]
pub(crate) struct Enrollment {
    pub id: String,
    pub upn: String,
    pub state: u32,
    pub last_success: Option<Date>,
    pub last_failure: Option<Date>,
}

/// A local account GCPW has associated with a Google account.
#[derive(Debug, Clone, PartialEq, Eq)]
pub(crate) struct GcpwUser {
    pub email: String,
    /// When the account's profile was last loaded. None when Windows does not say.
    pub last_used: Option<Date>,
}

/// What `GetDeviceRegistrationInfo` says: the registration GCPW looks at.
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub(crate) struct Registration {
    pub registered: bool,
    pub upn: String,
    pub service_uri: String,
}

impl Registration {
    fn is_google(&self) -> bool {
        self.registered && self.service_uri.to_ascii_lowercase().contains(GOOGLE_DISCOVERY_HOST)
    }
}

/// Everything `judge` decides from.
#[derive(Debug, Clone, PartialEq, Eq)]
pub(crate) struct Snapshot {
    pub gcpw_installed: bool,
    pub google: Vec<Enrollment>,
    pub gcpw_users: Vec<GcpwUser>,
    pub today: Date,
}

/// The PC, behind a trait so the decisions can be tested without a registry.
pub(crate) trait MdmSystem {
    fn snapshot(&self) -> std::io::Result<Snapshot>;
    fn registration(&self) -> Result<Registration, String>;
    /// Unregisters one enrolment. The HRESULT, which may be `TIMED_OUT`: see `system::unregister`.
    fn unregister(&self, id: &str) -> i32;
}

/// `HRESULT_FROM_WIN32(ERROR_TIMEOUT)`: the call had not returned when `system::unregister` stopped
/// waiting. Normal for a dead enrolment, so the outcome is judged by what Windows reports afterwards.
pub(crate) const TIMED_OUT: i32 = 0x800705B4_u32 as i32;

/// What one `google-mdm` item is on this PC, after unregistering a dead enrolment if `enforce` and
/// every guard holds.
pub(crate) fn judge(key: &str, data: Option<&str>, enforce: bool, system: &dyn MdmSystem) -> ItemResult {
    let result = |state, observed: Option<String>, detail: String, corrected| ItemResult {
        key: key.to_owned(),
        state,
        observed: observed.map(|o| truncate(&o, MAX_OBSERVED_CHARS)),
        detail: Some(truncate(&detail, MAX_DETAIL_CHARS)),
        subject: None,
        corrected,
    };

    let Some(stale_days) = parse_stale_days(data) else {
        return result(
            ERROR,
            None,
            format!("this agent reads a google-mdm item's data as a whole number of days from {MIN_STALE_DAYS} to {MAX_STALE_DAYS}"),
            false,
        );
    };
    let snapshot = match system.snapshot() {
        Ok(snapshot) => snapshot,
        Err(err) => return result(ERROR, None, format!("could not read the enrolments: {err}"), false),
    };

    let assessment = assess(&snapshot, stale_days);
    let Some(dead) = assessment.dead.clone() else {
        return result(assessment.state, assessment.observed, assessment.detail, false);
    };
    if !enforce {
        return result(assessment.state, assessment.observed, assessment.detail, false);
    }

    let registration = match system.registration() {
        Ok(registration) => registration,
        Err(err) => return result(DRIFTED, assessment.observed, format!("{}; left alone: {err}", assessment.detail), false),
    };
    let re_enroller = match plan_action(&snapshot, &dead, &registration) {
        Ok(person) => person,
        Err(why) => return result(DRIFTED, assessment.observed, format!("{}; left alone: {why}", assessment.detail), false),
    };

    let hr = system.unregister(&dead.id);
    let after = system.snapshot();
    let after_registration = system.registration().unwrap_or_default();
    let still_live = after
        .as_ref()
        .map(|s| s.google.iter().any(|e| e.id == dead.id && e.state == ENROLLMENT_LIVE))
        .unwrap_or(true);
    if still_live || after_registration.is_google() {
        return result(
            ERROR,
            assessment.observed,
            format!("tried to unregister the dead enrolment made by {} (HRESULT 0x{hr:08X}), but it is still registered", dead.upn),
            true,
        );
    }

    crate::logging::info(&format!("google-mdm: unregistered the dead enrolment made by {} (HRESULT 0x{hr:08X})", dead.upn));
    result(
        DRIFTED,
        Some("not enrolled".into()),
        format!(
            "unregistered the dead enrolment made by {} ({}); GCPW enrols this PC again at the next Google sign-in, which {re_enroller} made most recently",
            dead.upn, assessment.detail
        ),
        true,
    )
}

/// The verdict on a snapshot, before anything is done about it.
#[derive(Debug, Clone, PartialEq, Eq)]
pub(crate) struct Assessment {
    pub state: &'static str,
    pub observed: Option<String>,
    pub detail: String,
    /// The live enrolment, when it is dead: no successful sync for longer than the item allows, and a
    /// recent refusal. Only then may `enforce` act.
    pub dead: Option<Enrollment>,
}

pub(crate) fn assess(snapshot: &Snapshot, stale_days: i64) -> Assessment {
    let verdict = |state, observed: Option<String>, detail: String| Assessment { state, observed, detail, dead: None };

    if !snapshot.gcpw_installed {
        return verdict(NOT_APPLICABLE, None, "GCPW is not installed, so nothing here enrols with Google".into());
    }

    let live: Vec<&Enrollment> = snapshot.google.iter().filter(|e| e.state == ENROLLMENT_LIVE).collect();
    let enrollment = match live.as_slice() {
        [] => {
            return verdict(
                DRIFTED,
                Some("not enrolled".into()),
                "no live Google enrolment; GCPW enrols this PC at the next Google sign-in".into(),
            )
        }
        [one] => *one,
        several => {
            return verdict(
                ERROR,
                Some(format!("{} live Google enrolments", several.len())),
                "more than one live Google enrolment; nothing is done about any of them".into(),
            )
        }
    };

    let observed = Some(format!(
        "enrolled-by={}; last-ok={}; last-fail={}",
        enrollment.upn,
        show(enrollment.last_success),
        show(enrollment.last_failure)
    ));
    let since_success = enrollment.last_success.map(|d| days_between(d, snapshot.today));
    if since_success.is_some_and(|days| days <= stale_days) {
        return verdict(COMPLIANT, observed, format!("last synced with Google {} day(s) ago", since_success.unwrap_or(0)));
    }

    let quiet = match since_success {
        Some(days) => format!("no successful sync for {days} days"),
        None => "no successful sync recorded".into(),
    };
    let refused_recently = enrollment.last_failure.is_some_and(|failure| {
        days_between(failure, snapshot.today) <= RECENT_FAILURE_DAYS && enrollment.last_success.is_none_or(|success| failure >= success)
    });
    if !refused_recently {
        return verdict(DRIFTED, observed, format!("{quiet}, and no recent attempt: the PC may have been offline"));
    }

    Assessment {
        state: DRIFTED,
        observed,
        detail: format!("{quiet}; Google refuses every sync, so the account that enrolled it is probably gone"),
        dead: Some(enrollment.clone()),
    }
}

/// The guards `enforce` must pass before unregistering `dead`. The person most likely to re-enrol
/// the PC when they hold, and why not when they do not.
pub(crate) fn plan_action(snapshot: &Snapshot, dead: &Enrollment, registration: &Registration) -> Result<String, String> {
    // What GCPW sees must be this very enrolment, or GCPW would not re-enrol after it goes.
    if !registration.is_google() || !registration.upn.eq_ignore_ascii_case(&dead.upn) {
        return Err(format!(
            "Windows reports the registration as '{}' at '{}', not this Google enrolment",
            registration.upn, registration.service_uri
        ));
    }

    let mut others: Vec<&GcpwUser> = snapshot
        .gcpw_users
        .iter()
        .filter(|u| !u.email.eq_ignore_ascii_case(&dead.upn))
        .filter(|u| u.last_used.is_some_and(|d| days_between(d, snapshot.today) <= OTHER_USER_RECENT_DAYS))
        .collect();
    others.sort_by_key(|u| std::cmp::Reverse(u.last_used));
    match others.first() {
        Some(person) => Ok(person.email.clone()),
        None => Err(format!(
            "nobody but {} has used this PC with Google in {OTHER_USER_RECENT_DAYS} days, so nobody would enrol it again",
            dead.upn
        )),
    }
}

fn parse_stale_days(data: Option<&str>) -> Option<i64> {
    let data = data?;
    if data.is_empty() || data.len() > 2 || !data.bytes().all(|b| b.is_ascii_digit()) {
        return None;
    }
    let days: i64 = data.parse().ok()?;
    (MIN_STALE_DAYS..=MAX_STALE_DAYS).contains(&days).then_some(days)
}

/// The date of an OMA-DM `ConnInfo` time such as `20261124T074704Z`. Only the date is used: the time is
/// device-local even though it ends in `Z`.
pub(crate) fn parse_conninfo_date(text: &str) -> Option<Date> {
    let digits = text.get(0..8)?;
    if !digits.bytes().all(|b| b.is_ascii_digit()) {
        return None;
    }
    let year: i32 = digits[0..4].parse().ok()?;
    let month: u8 = digits[4..6].parse().ok()?;
    let day: u8 = digits[6..8].parse().ok()?;
    Date::from_calendar_date(year, Month::try_from(month).ok()?, day).ok()
}

/// A FILETIME (100 ns intervals since 1601-01-01 UTC) as a date.
pub(crate) fn filetime_date(high: u32, low: u32) -> Option<Date> {
    let ticks = ((high as u64) << 32) | low as u64;
    if ticks == 0 {
        return None;
    }
    const UNIX_EPOCH_IN_TICKS: u64 = 116_444_736_000_000_000;
    let seconds = (ticks.checked_sub(UNIX_EPOCH_IN_TICKS)? / 10_000_000) as i64;
    OffsetDateTime::from_unix_timestamp(seconds).ok().map(|t| t.date())
}

fn days_between(earlier: Date, later: Date) -> i64 {
    (later - earlier).whole_days()
}

fn show(date: Option<Date>) -> String {
    date.map(|d| format!("{:04}-{:02}-{:02}", d.year(), u8::from(d.month()), d.day())).unwrap_or_else(|| "never".into())
}

fn truncate(text: &str, max: usize) -> String {
    text.chars().take(max).collect()
}

fn is_google(provider: &str, url: &str) -> bool {
    provider == "Google" || url.to_ascii_lowercase().contains(GOOGLE_DISCOVERY_HOST)
}

/// The real PC.
#[cfg(windows)]
pub(crate) mod system {
    use std::sync::mpsc;
    use std::time::Duration;

    use time::OffsetDateTime;
    use winreg::enums::{HKEY_LOCAL_MACHINE, KEY_READ, KEY_WOW64_64KEY};
    use winreg::RegKey;

    use super::{filetime_date, is_google, parse_conninfo_date, Enrollment, GcpwUser, MdmSystem, Registration, Snapshot, TIMED_OUT};

    const ENROLLMENTS: &str = r"SOFTWARE\Microsoft\Enrollments";
    const OMADM_ACCOUNTS: &str = r"SOFTWARE\Microsoft\Provisioning\OMADM\Accounts";
    const GCPW: &str = r"SOFTWARE\Google\GCPW";
    const GCPW_USERS: &str = r"SOFTWARE\Google\GCPW\Users";
    const PROFILE_LIST: &str = r"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList";

    /// How long `unregister` waits. On a dead enrolment Windows drops it at once and then waits on
    /// Google for the final unenrolment report, which never comes (five minutes and still waiting, on
    /// the PC this was proven on), so the outcome is read from the registry afterwards instead.
    const UNREGISTER_WAIT: Duration = Duration::from_secs(60);
    /// The policy rollback finishes after the call; give it a moment before reading the result.
    const SETTLE: Duration = Duration::from_secs(20);

    pub(crate) struct System;

    fn open(path: &str) -> std::io::Result<Option<RegKey>> {
        match RegKey::predef(HKEY_LOCAL_MACHINE).open_subkey_with_flags(path, KEY_READ | KEY_WOW64_64KEY) {
            Ok(key) => Ok(Some(key)),
            Err(err) if err.kind() == std::io::ErrorKind::NotFound => Ok(None),
            Err(err) => Err(err),
        }
    }

    fn is_guid(name: &str) -> bool {
        name.len() == 36 && name.bytes().all(|b| b.is_ascii_hexdigit() || b == b'-')
    }

    impl MdmSystem for System {
        fn snapshot(&self) -> std::io::Result<Snapshot> {
            let mut google = Vec::new();
            if let Some(enrollments) = open(ENROLLMENTS)? {
                for id in enrollments.enum_keys().filter_map(Result::ok).filter(|n| is_guid(n)) {
                    let Ok(key) = enrollments.open_subkey_with_flags(&id, KEY_READ | KEY_WOW64_64KEY) else { continue };
                    let provider: String = key.get_value("ProviderID").unwrap_or_default();
                    let url: String = key.get_value("DiscoveryServiceFullURL").unwrap_or_default();
                    if !is_google(&provider, &url) {
                        continue;
                    }
                    let conninfo = open(&format!(r"{OMADM_ACCOUNTS}\{id}\Protected\ConnInfo"))?;
                    let time = |name: &str| conninfo.as_ref().and_then(|k| k.get_value::<String, _>(name).ok()).and_then(|t| parse_conninfo_date(&t));
                    google.push(Enrollment {
                        upn: key.get_value("UPN").unwrap_or_default(),
                        state: key.get_value("EnrollmentState").unwrap_or(0),
                        last_success: time("ServerLastSuccessTime"),
                        last_failure: time("ServerLastFailureTime"),
                        id,
                    });
                }
            }

            let profiles = open(PROFILE_LIST)?;
            let mut gcpw_users = Vec::new();
            if let Some(users) = open(GCPW_USERS)? {
                for sid in users.enum_keys().filter_map(Result::ok) {
                    let Ok(key) = users.open_subkey_with_flags(&sid, KEY_READ | KEY_WOW64_64KEY) else { continue };
                    let Ok(email) = key.get_value::<String, _>("email") else { continue };
                    let last_used = profiles.as_ref().and_then(|p| p.open_subkey_with_flags(&sid, KEY_READ | KEY_WOW64_64KEY).ok()).and_then(|p| {
                        let high: u32 = p.get_value("LocalProfileLoadTimeHigh").ok()?;
                        let low: u32 = p.get_value("LocalProfileLoadTimeLow").ok()?;
                        filetime_date(high, low)
                    });
                    gcpw_users.push(GcpwUser { email, last_used });
                }
            }

            Ok(Snapshot { gcpw_installed: open(GCPW)?.is_some(), google, gcpw_users, today: OffsetDateTime::now_utc().date() })
        }

        fn registration(&self) -> Result<Registration, String> {
            // SAFETY: see `registration_info`.
            unsafe { registration_info() }
        }

        fn unregister(&self, id: &str) -> i32 {
            let id = id.to_owned();
            let (sender, receiver) = mpsc::channel();
            // Its own thread, initialised for the multithreaded apartment, as Microsoft's own Intune
            // remediation sample runs this call. If it outlives the wait, it finishes on its own.
            std::thread::spawn(move || {
                // SAFETY: see `unregister_on_this_thread`.
                let hr = unsafe { unregister_on_this_thread(&id) };
                let _ = sender.send(hr);
            });
            let hr = receiver.recv_timeout(UNREGISTER_WAIT).unwrap_or(TIMED_OUT);
            std::thread::sleep(SETTLE);
            hr
        }
    }

    /// `mdmregistration.dll`, from System32 only, as `secure_attention::send_sas` loads sas.dll: the
    /// default search order would look in this executable's directory first.
    unsafe fn mdm_function(name: &[u8]) -> Result<unsafe extern "system" fn() -> isize, String> {
        use windows_sys::Win32::Foundation::GetLastError;
        use windows_sys::Win32::System::LibraryLoader::{GetProcAddress, LoadLibraryExW, LOAD_LIBRARY_SEARCH_SYSTEM32};

        let library = crate::win32::wide("mdmregistration.dll");
        let module = LoadLibraryExW(library.as_ptr(), std::ptr::null_mut(), LOAD_LIBRARY_SEARCH_SYSTEM32);
        if module.is_null() {
            return Err(format!("mdmregistration.dll would not load (error {})", GetLastError()));
        }
        GetProcAddress(module, name.as_ptr()).ok_or_else(|| format!("mdmregistration.dll has no {} (error {})", String::from_utf8_lossy(&name[..name.len() - 1]), GetLastError()))
    }

    /// `MANAGEMENT_REGISTRATION_INFO` (mdmregistration.h).
    #[repr(C)]
    struct ManagementRegistrationInfo {
        registered: i32,
        kind: u32,
        upn: *const u16,
        service_uri: *const u16,
    }

    /// `DeviceRegistrationBasicInfo` in `REGISTRATION_INFORMATION_CLASS`.
    const DEVICE_REGISTRATION_BASIC_INFO: i32 = 1;

    /// # Safety
    ///
    /// Calls `HRESULT GetDeviceRegistrationInfo(REGISTRATION_INFORMATION_CLASS, PVOID*)` through a pointer of
    /// that signature. On success the function allocates a `MANAGEMENT_REGISTRATION_INFO` on the process heap,
    /// which is read and then freed with `HeapFree`, as GCPW does.
    unsafe fn registration_info() -> Result<Registration, String> {
        use windows_sys::Win32::System::Memory::{GetProcessHeap, HeapFree};

        let function = mdm_function(b"GetDeviceRegistrationInfo\0")?;
        let get: unsafe extern "system" fn(i32, *mut *mut ManagementRegistrationInfo) -> i32 = std::mem::transmute(function);
        let mut info: *mut ManagementRegistrationInfo = std::ptr::null_mut();
        let hr = get(DEVICE_REGISTRATION_BASIC_INFO, &mut info);
        if hr < 0 || info.is_null() {
            // Not registered with anything reads as a failure here, which is not an error to report.
            return Ok(Registration::default());
        }
        let registration = Registration {
            registered: (*info).registered != 0,
            upn: read_wide((*info).upn),
            service_uri: read_wide((*info).service_uri),
        };
        HeapFree(GetProcessHeap(), 0, info as *const _);
        Ok(registration)
    }

    /// # Safety
    ///
    /// `text` is null or a NUL-terminated UTF-16 string that stays valid for the call.
    unsafe fn read_wide(text: *const u16) -> String {
        if text.is_null() {
            return String::new();
        }
        let mut length = 0;
        while *text.add(length) != 0 {
            length += 1;
        }
        String::from_utf16_lossy(std::slice::from_raw_parts(text, length))
    }

    /// # Safety
    ///
    /// Calls `HRESULT UnregisterDeviceWithManagement(LPCWSTR enrollmentID)` through a pointer of that
    /// signature, with a NUL-terminated id that outlives the call. COM is initialised for this thread
    /// first and uninitialised after, only when that initialisation succeeded.
    unsafe fn unregister_on_this_thread(id: &str) -> i32 {
        use windows_sys::Win32::System::Com::{CoInitializeEx, CoUninitialize, COINIT_MULTITHREADED};

        let com = CoInitializeEx(std::ptr::null(), COINIT_MULTITHREADED as _);
        let hr = match mdm_function(b"UnregisterDeviceWithManagement\0") {
            Ok(function) => {
                let unregister: unsafe extern "system" fn(*const u16) -> i32 = std::mem::transmute(function);
                let id = crate::win32::wide(id);
                unregister(id.as_ptr())
            }
            Err(err) => {
                crate::logging::warn(&format!("google-mdm: {err}"));
                0x80004005_u32 as i32
            }
        };
        if com >= 0 {
            CoUninitialize();
        }
        hr
    }
}

#[cfg(test)]
mod tests {
    use std::cell::RefCell;

    use time::macros::date;

    use super::*;

    const TODAY: Date = date!(2026 - 11 - 24);

    fn dead() -> Enrollment {
        Enrollment {
            id: "AAAAAAAA-1111-4111-8111-111111111111".into(),
            upn: "former@example.com".into(),
            state: 1,
            last_success: Some(date!(2025 - 11 - 05)),
            last_failure: Some(TODAY),
        }
    }

    fn healthy() -> Enrollment {
        Enrollment { last_success: Some(date!(2026 - 11 - 23)), last_failure: None, upn: "current@example.com".into(), ..dead() }
    }

    fn current_user() -> GcpwUser {
        GcpwUser { email: "current@example.com".into(), last_used: Some(TODAY) }
    }

    fn snapshot(google: Vec<Enrollment>) -> Snapshot {
        Snapshot {
            gcpw_installed: true,
            google,
            gcpw_users: vec![GcpwUser { email: "former@example.com".into(), last_used: Some(date!(2025 - 10 - 09)) }, current_user()],
            today: TODAY,
        }
    }

    fn registered_as(upn: &str) -> Registration {
        Registration { registered: true, upn: upn.into(), service_uri: "https://deviceenrollmentforwindows.googleapis.com/v1/discovery".into() }
    }

    /// A PC whose enrolment `unregister` turns from live to unregistered (state 4), as Windows did.
    struct Fake {
        before: Snapshot,
        after: Snapshot,
        registration_before: Registration,
        registration_after: Registration,
        unregistered: RefCell<Vec<String>>,
    }

    impl Fake {
        fn dead_pc() -> Self {
            let before = snapshot(vec![dead()]);
            let after = snapshot(vec![Enrollment { state: 4, ..dead() }]);
            Fake {
                before,
                after,
                registration_before: registered_as("former@example.com"),
                registration_after: Registration::default(),
                unregistered: RefCell::new(Vec::new()),
            }
        }
    }

    impl MdmSystem for Fake {
        fn snapshot(&self) -> std::io::Result<Snapshot> {
            Ok(if self.unregistered.borrow().is_empty() { self.before.clone() } else { self.after.clone() })
        }
        fn registration(&self) -> Result<Registration, String> {
            Ok(if self.unregistered.borrow().is_empty() { self.registration_before.clone() } else { self.registration_after.clone() })
        }
        fn unregister(&self, id: &str) -> i32 {
            self.unregistered.borrow_mut().push(id.into());
            TIMED_OUT
        }
    }

    #[test]
    fn a_recent_successful_sync_is_compliant() {
        let found = assess(&snapshot(vec![healthy()]), 14);
        assert_eq!(found.state, COMPLIANT);
        assert_eq!(found.dead, None);
        assert_eq!(found.observed.as_deref(), Some("enrolled-by=current@example.com; last-ok=2026-11-23; last-fail=never"));
    }

    #[test]
    fn a_year_of_refused_syncs_is_dead() {
        let found = assess(&snapshot(vec![dead()]), 14);
        assert_eq!(found.state, DRIFTED);
        assert_eq!(found.dead, Some(dead()));
        assert!(found.detail.contains("no successful sync for 384 days"), "{}", found.detail);
    }

    #[test]
    fn stale_without_a_recent_attempt_is_drifted_but_never_dead() {
        let offline = Enrollment { last_failure: Some(date!(2026 - 10 - 01)), ..dead() };
        let found = assess(&snapshot(vec![offline]), 14);
        assert_eq!(found.state, DRIFTED);
        assert_eq!(found.dead, None);
        assert!(found.detail.contains("may have been offline"));
    }

    #[test]
    fn a_failure_older_than_the_last_success_is_not_a_refusal() {
        let flaky = Enrollment { last_success: Some(date!(2026 - 11 - 23)), last_failure: Some(date!(2026 - 11 - 22)), ..dead() };
        assert_eq!(assess(&snapshot(vec![flaky]), 14).state, COMPLIANT);
    }

    #[test]
    fn leftovers_in_state_4_are_ignored() {
        let leftover = Enrollment { state: 4, id: "CCCCCCCC-3333-4333-8333-333333333333".into(), ..dead() };
        let found = assess(&snapshot(vec![leftover, healthy()]), 14);
        assert_eq!(found.state, COMPLIANT);
    }

    #[test]
    fn no_live_enrolment_is_drifted_with_nothing_to_do() {
        let found = assess(&snapshot(vec![Enrollment { state: 4, ..dead() }]), 14);
        assert_eq!(found.state, DRIFTED);
        assert_eq!(found.observed.as_deref(), Some("not enrolled"));
        assert_eq!(found.dead, None);
    }

    #[test]
    fn two_live_enrolments_are_an_error_and_left_alone() {
        let found = assess(&snapshot(vec![dead(), healthy()]), 14);
        assert_eq!(found.state, ERROR);
        assert_eq!(found.dead, None);
    }

    #[test]
    fn without_gcpw_it_is_not_applicable() {
        let found = assess(&Snapshot { gcpw_installed: false, ..snapshot(vec![dead()]) }, 14);
        assert_eq!(found.state, NOT_APPLICABLE);
        assert_eq!(found.dead, None);
    }

    #[test]
    fn the_plan_names_the_most_recent_other_user() {
        let mut pc = snapshot(vec![dead()]);
        pc.gcpw_users.push(GcpwUser { email: "colleague@example.com".into(), last_used: Some(date!(2026 - 11 - 18)) });
        assert_eq!(plan_action(&pc, &dead(), &registered_as("FORMER@example.com")), Ok("current@example.com".into()));
    }

    #[test]
    fn a_leavers_pc_is_left_alone() {
        let mut pc = snapshot(vec![dead()]);
        pc.gcpw_users = vec![
            GcpwUser { email: "former@example.com".into(), last_used: Some(TODAY) },
            GcpwUser { email: "someone@example.com".into(), last_used: Some(date!(2026 - 10 - 01)) },
            GcpwUser { email: "unknown@example.com".into(), last_used: None },
        ];
        assert!(plan_action(&pc, &dead(), &registered_as("former@example.com")).is_err());
    }

    #[test]
    fn a_registration_gcpw_does_not_see_as_this_enrolment_is_left_alone() {
        let pc = snapshot(vec![dead()]);
        assert!(plan_action(&pc, &dead(), &registered_as("someone-else@example.com")).is_err());
        assert!(plan_action(&pc, &dead(), &Registration::default()).is_err());
        let elsewhere = Registration { service_uri: "https://mdm.example.com/enroll".into(), ..registered_as("former@example.com") };
        assert!(plan_action(&pc, &dead(), &elsewhere).is_err());
    }

    #[test]
    fn audit_reports_a_dead_enrolment_and_unregisters_nothing() {
        let pc = Fake::dead_pc();
        let result = judge("google.mdm", Some("14"), false, &pc);
        assert_eq!(result.state, DRIFTED);
        assert!(!result.corrected);
        assert!(pc.unregistered.borrow().is_empty());
    }

    #[test]
    fn enforce_unregisters_only_the_dead_enrolment_and_reports_the_correction() {
        let pc = Fake::dead_pc();
        let result = judge("google.mdm", Some("14"), true, &pc);
        assert_eq!(*pc.unregistered.borrow(), vec![dead().id]);
        assert_eq!(result.state, DRIFTED);
        assert!(result.corrected);
        assert_eq!(result.observed.as_deref(), Some("not enrolled"));
        let detail = result.detail.unwrap();
        assert!(detail.contains("unregistered the dead enrolment made by former@example.com"), "{detail}");
        assert!(detail.contains("current@example.com"), "{detail}");
    }

    #[test]
    fn enforce_reports_an_error_when_the_enrolment_survives() {
        let mut pc = Fake::dead_pc();
        pc.after = pc.before.clone();
        pc.registration_after = pc.registration_before.clone();
        let result = judge("google.mdm", Some("14"), true, &pc);
        assert_eq!(result.state, ERROR);
        assert!(result.corrected);
    }

    #[test]
    fn enforce_leaves_a_healthy_enrolment_alone() {
        let mut pc = Fake::dead_pc();
        pc.before = snapshot(vec![healthy()]);
        let result = judge("google.mdm", Some("14"), true, &pc);
        assert_eq!(result.state, COMPLIANT);
        assert!(pc.unregistered.borrow().is_empty());
    }

    #[test]
    fn enforce_leaves_a_leavers_pc_alone() {
        let mut pc = Fake::dead_pc();
        pc.before.gcpw_users.retain(|u| u.email == "former@example.com");
        let result = judge("google.mdm", Some("14"), true, &pc);
        assert_eq!(result.state, DRIFTED);
        assert!(!result.corrected);
        assert!(result.detail.unwrap().contains("left alone"));
        assert!(pc.unregistered.borrow().is_empty());
    }

    #[test]
    fn stale_days_outside_the_range_is_an_error_and_nothing_is_read() {
        for data in [None, Some(""), Some("2"), Some("91"), Some("014"), Some("-5"), Some("x")] {
            let pc = Fake::dead_pc();
            assert_eq!(judge("google.mdm", data, true, &pc).state, ERROR, "{data:?}");
            assert!(pc.unregistered.borrow().is_empty());
        }
    }

    #[test]
    fn conninfo_times_are_read_as_dates() {
        assert_eq!(parse_conninfo_date("20251105T154721Z"), Some(date!(2025 - 11 - 05)));
        assert_eq!(parse_conninfo_date("20261124T074706Z"), Some(date!(2026 - 11 - 24)));
        assert_eq!(parse_conninfo_date(""), None);
        assert_eq!(parse_conninfo_date("2026-11-24"), None);
        assert_eq!(parse_conninfo_date("20261332T000000Z"), None);
    }

    #[test]
    fn filetimes_are_read_as_dates() {
        // 2026-11-24T00:00:00Z is 1795478400 seconds after the Unix epoch.
        let ticks: u64 = 116_444_736_000_000_000 + 1_795_478_400 * 10_000_000;
        assert_eq!(filetime_date((ticks >> 32) as u32, ticks as u32), Some(TODAY));
        assert_eq!(filetime_date(0, 0), None);
    }

    #[test]
    fn google_is_recognised_by_provider_or_discovery_url() {
        assert!(is_google("Google", ""));
        assert!(is_google("", "https://DeviceEnrollmentForWindows.googleapis.com/v1/discovery"));
        assert!(!is_google("MEMDM", "https://mdm.example.com/enroll"));
    }
}
