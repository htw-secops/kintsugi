//! The few Win32 primitives the two UI modules share.
//!
//! This agent talks to Win32 directly rather than through a windowing crate, for the same reason
//! the macOS agent talks to AppKit directly: what it actually needs is a notification-area icon, a
//! small progress window, and one timed dialog, and a general-purpose windowing layer brings a
//! large dependency (and its own event-loop model) for three windows. Everything here is a thin,
//! single-purpose wrapper — no abstraction, just the unsafe kept in one place with the invariants
//! written down.

use windows_sys::core::PCWSTR;
use windows_sys::Win32::Foundation::{HINSTANCE, HWND, LPARAM, RECT, WPARAM};
use windows_sys::Win32::Graphics::Gdi::HGDIOBJ;
use windows_sys::Win32::System::LibraryLoader::GetModuleHandleW;
use windows_sys::Win32::UI::WindowsAndMessaging::{
    GetSystemMetrics, SetWindowPos, SystemParametersInfoW, HWND_TOP, SM_CXSCREEN, SM_CYSCREEN, SPI_GETWORKAREA, SWP_NOSIZE,
    SWP_NOZORDER,
};

/// Encodes `value` as a NUL-terminated UTF-16 string, the form every `...W` Win32 entry point takes.
///
/// The returned `Vec` must outlive the call it's passed to — Win32 copies nothing. Every caller
/// here binds it to a local that lives to the end of the enclosing block for exactly that reason.
pub fn wide(value: &str) -> Vec<u16> {
    value.encode_utf16().chain(std::iter::once(0)).collect()
}

/// This executable's module handle — what every window class registration and window creation is
/// attributed to. `GetModuleHandleW(null)` returns the handle of the running process and cannot
/// fail for that argument.
pub fn instance() -> HINSTANCE {
    // SAFETY: GetModuleHandleW(null) asks for the calling process's own module, which always
    // exists; it neither allocates nor takes ownership of anything.
    unsafe { GetModuleHandleW(std::ptr::null()) as HINSTANCE }
}

/// The primary display's work area: the screen less the taskbar and any docked app bars, in the
/// calling thread's DPI coordinates. The whole screen if Windows will not say.
pub fn work_area() -> RECT {
    let mut area = RECT { left: 0, top: 0, right: 0, bottom: 0 };
    // SAFETY: SPI_GETWORKAREA writes exactly one RECT through the pointer, which is a live local of
    // that type; GetSystemMetrics reads system state only and cannot fail for these indices.
    unsafe {
        if SystemParametersInfoW(SPI_GETWORKAREA, 0, &mut area as *mut RECT as *mut core::ffi::c_void, 0) == 0 {
            area = RECT { left: 0, top: 0, right: GetSystemMetrics(SM_CXSCREEN), bottom: GetSystemMetrics(SM_CYSCREEN) };
        }
    }
    area
}

/// Positions a window in the middle of the primary display's work area, and never above or left of
/// it: one taller than the work area keeps its title bar on screen instead of being centred off the
/// top, with the taskbar over its buttons.
///
/// The primary display rather than the work area of whichever monitor the cursor is on: these
/// windows appear without the user having asked for them (a patch is starting), so the predictable
/// place is the main screen, not wherever the pointer happens to be.
pub fn center_on_screen(hwnd: HWND, width: i32, height: i32) {
    let area = work_area();
    let x = (area.left + (area.right - area.left - width) / 2).max(area.left);
    let y = (area.top + (area.bottom - area.top - height) / 2).max(area.top);
    // SAFETY: SetWindowPos on a live window with SWP_NOSIZE ignores the size arguments it's still
    // required to be passed.
    unsafe {
        SetWindowPos(hwnd, HWND_TOP, x, y, 0, 0, SWP_NOSIZE | SWP_NOZORDER);
    }
}

/// A window class name, registered at most once per process.
///
/// `RegisterClassW` fails if the same name is already registered, so a module that creates its
/// window more than once (the dialog does — every patch cycle shows one) has to register lazily and
/// exactly once rather than per window.
pub struct WindowClass {
    name: Vec<u16>,
}

impl WindowClass {
    pub fn name(&self) -> PCWSTR {
        self.name.as_ptr()
    }
}

/// Registers `name` with `wnd_proc`, returning a handle to the class name to create windows with.
///
/// # Safety
///
/// `wnd_proc` must be a valid window procedure: it is called by the system on the thread that owns
/// each window of this class, for the life of the process.
pub unsafe fn register_class(
    name: &str,
    wnd_proc: unsafe extern "system" fn(HWND, u32, WPARAM, LPARAM) -> isize,
    background: HGDIOBJ,
) -> WindowClass {
    use windows_sys::Win32::UI::WindowsAndMessaging::{
        LoadCursorW, RegisterClassW, CS_HREDRAW, CS_VREDRAW, IDC_ARROW, WNDCLASSW,
    };

    let name = wide(name);

    let class = WNDCLASSW {
        style: CS_HREDRAW | CS_VREDRAW,
        lpfnWndProc: Some(wnd_proc),
        cbClsExtra: 0,
        cbWndExtra: 0,
        hInstance: instance(),
        hIcon: std::ptr::null_mut(),
        hCursor: LoadCursorW(std::ptr::null_mut(), IDC_ARROW),
        hbrBackground: background as _,
        lpszMenuName: std::ptr::null(),
        lpszClassName: name.as_ptr(),
    };

    // A zero return means registration failed — almost always "this name is already registered",
    // which for our own uniquely-named classes can only happen if a caller registered twice.
    // Creating a window with the name still works in that case, so this is deliberately not fatal.
    RegisterClassW(&class);

    WindowClass { name }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn wide_is_nul_terminated() {
        // Every ...W entry point reads until the terminator; without it they read past the
        // allocation.
        let encoded = wide("hi");

        assert_eq!(encoded, vec![b'h' as u16, b'i' as u16, 0]);
    }

    #[test]
    fn wide_encodes_an_empty_string_as_just_the_terminator() {
        assert_eq!(wide(""), vec![0]);
    }

    #[test]
    fn wide_encodes_characters_outside_the_basic_multilingual_plane_as_a_surrogate_pair() {
        // An application name can contain anything the vendor chose; a naive "one char, one u16"
        // encoding would truncate this one.
        let encoded = wide("\u{1F600}");

        assert_eq!(encoded.len(), 3);
        assert_eq!(encoded[2], 0);
    }
}
