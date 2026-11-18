//! Ctrl+Alt+Del for a remote screen session: the viewer's `secure-attention` message.
//!
//! # Why the service raises it, not the session helper
//!
//! Windows ignores an injected Ctrl+Alt+Del by design. The secure attention sequence is the one key
//! combination no program can fake, which is what lets a user trust that the screen it brings up is
//! really Windows. The sanctioned way round that is `SendSAS` (sas.dll), and it works only for a
//! caller that is a **service** or carries UIAccess. The session helper is SYSTEM but neither, so
//! `remote_control::relay` takes this one message off the session socket and calls it from the
//! service instead of forwarding it down the pipe.
//!
//! # The policy the agent sets for itself
//!
//! Even a service is refused — silently, `SendSAS` returns nothing — unless the machine policy
//! `SoftwareSASGeneration` allows services. Unset, it allows only Ease of Access applications on
//! the secure desktop, which is what lets the on-screen keyboard press Ctrl+Alt+Del at the sign-in
//! screen. So the agent widens it the first time the button is pressed, and only then: unset or
//! "Ease of Access" becomes "Services and Ease of Access" (3), keeping the on-screen keyboard
//! working; "None" (0) becomes "Services" (1). A Group Policy that sets it will put its own value
//! back at the next refresh, which is the organisation's call to make — the log says what was found.

/// Where Group Policy's "Disable or enable software Secure Attention Sequence" lives.
const POLICY_KEY: &str = r"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System";
const POLICY_VALUE: &str = "SoftwareSASGeneration";

/// What to set `SoftwareSASGeneration` to so that a service may call `SendSAS`, or `None` to leave
/// it alone. Bit 0 is services and bit 1 is Ease of Access, so this only ever adds services.
///
/// Unset is not the same as 0: Windows reads unset as "Ease of Access applications on the secure
/// desktop", and writing 1 over it would take Ctrl+Alt+Del away from the sign-in screen's on-screen
/// keyboard. An undefined value is replaced with 1, the narrowest one that works.
pub fn policy_update(current: Option<u32>) -> Option<u32> {
    match current {
        Some(1) | Some(3) => None,
        None | Some(2) => Some(3),
        Some(_) => Some(1),
    }
}

/// Raises Ctrl+Alt+Del on the console session, setting the policy first if it would refuse.
/// Every outcome is logged: `SendSAS` reports nothing, so the log is the only trace.
#[cfg(windows)]
pub fn raise() {
    use winreg::enums::{HKEY_LOCAL_MACHINE, KEY_READ, KEY_SET_VALUE, KEY_WOW64_64KEY};
    use winreg::RegKey;

    use crate::logging;

    match RegKey::predef(HKEY_LOCAL_MACHINE).open_subkey_with_flags(POLICY_KEY, KEY_READ | KEY_SET_VALUE | KEY_WOW64_64KEY) {
        Ok(key) => {
            // A value of the wrong type reads as unset, and is replaced with a DWORD.
            let current = key.get_value::<u32, _>(POLICY_VALUE).ok();
            if let Some(next) = policy_update(current) {
                let was = current.map_or_else(|| "unset".to_string(), |value| value.to_string());
                match key.set_value(POLICY_VALUE, &next) {
                    Ok(()) => logging::info(&format!("{POLICY_VALUE} changed from {was} to {next} so this service may raise Ctrl+Alt+Del")),
                    Err(err) => logging::warn(&format!("could not change {POLICY_VALUE} from {was} to {next}: {err}; Windows may ignore Ctrl+Alt+Del")),
                }
            }
        }
        // Still tried: the policy may already allow it.
        Err(err) => logging::warn(&format!("could not open {POLICY_KEY} to check {POLICY_VALUE}: {err}")),
    }

    // SAFETY: see `send_sas`; it takes no arguments from the viewer.
    match unsafe { send_sas() } {
        Ok(()) => logging::info("Ctrl+Alt+Del requested by the remote viewer; SendSAS called"),
        Err(err) => logging::error(&format!("could not raise Ctrl+Alt+Del: {err}")),
    }
}

/// Loads `SendSAS` from sas.dll and calls it as a service (`AsUser` false).
///
/// Loaded at run time because windows-sys does not bind it, and from System32 only: a bare name would
/// search this executable's own directory first, and a sas.dll planted there would run as SYSTEM. The
/// library is left loaded: it is a system DLL, and the next press would only load it again.
///
/// # Safety
///
/// Calls a system function through a pointer whose signature is `VOID SendSAS(BOOL AsUser)` (sas.h).
#[cfg(windows)]
unsafe fn send_sas() -> Result<(), String> {
    use windows_sys::Win32::Foundation::GetLastError;
    use windows_sys::Win32::System::LibraryLoader::{GetProcAddress, LoadLibraryExW, LOAD_LIBRARY_SEARCH_SYSTEM32};

    let library = crate::win32::wide("sas.dll");
    let module = LoadLibraryExW(library.as_ptr(), std::ptr::null_mut(), LOAD_LIBRARY_SEARCH_SYSTEM32);
    if module.is_null() {
        return Err(format!("sas.dll would not load (error {})", GetLastError()));
    }
    let Some(address) = GetProcAddress(module, b"SendSAS\0".as_ptr()) else {
        return Err(format!("sas.dll has no SendSAS (error {})", GetLastError()));
    };
    let send_sas: unsafe extern "system" fn(i32) = std::mem::transmute(address);
    send_sas(0);
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn unset_or_ease_of_access_only_gains_services_and_keeps_ease_of_access() {
        // Unset means "Ease of Access on the secure desktop"; 1 alone would take that away.
        assert_eq!(policy_update(None), Some(3));
        assert_eq!(policy_update(Some(2)), Some(3));
    }

    #[test]
    fn none_becomes_services_only() {
        assert_eq!(policy_update(Some(0)), Some(1));
    }

    #[test]
    fn a_value_that_already_allows_services_is_left_alone() {
        assert_eq!(policy_update(Some(1)), None);
        assert_eq!(policy_update(Some(3)), None);
    }

    #[test]
    fn an_undefined_value_becomes_services_only() {
        assert_eq!(policy_update(Some(7)), Some(1));
    }
}
