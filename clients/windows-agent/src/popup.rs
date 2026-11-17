//! The one look every window this agent puts in front of a user shares: the organisation's logo and
//! name (see `branding`), a bold title on white, Arial, a list or a callout, and pill buttons in a
//! grey footer. `dialogs` builds a [`Card`] for each question it asks, `progress_window` one for the
//! running cycle; this module turns a card into a window.
//!
//! # Why everything is drawn by hand
//!
//! It used to be stock Win32 controls — a STATIC for the text, BUTTONs, the common-controls progress
//! bar — and with no application manifest those render in the Windows 2000 style, in MS Sans Serif
//! 8pt, bitmap-stretched on any display above 100%. A user report about how dated the prompt looked
//! is what led here. A manifest would fix the bevels and nothing else, so the window paints itself:
//! GDI for text, and an antialiased rounded rectangle rasterised here (`rounded_rect_alpha`) and
//! composited with `AlphaBlend`, because GDI's own `RoundRect` has no antialiasing and a pill with
//! jagged ends looks worse than the bevel it replaced. The buttons stay real BUTTON windows, owner
//! drawn, so keyboard focus, Space and screen readers keep working.
//!
//! # DPI
//!
//! The process is DPI-unaware, deliberately: the session helper's GDI capture and pointer mapping
//! were built and tested that way (see `screen_capture`, `input_injection`). Each UI *thread* opts
//! in instead (`enable_dpi_awareness`), which leaves the capture thread exactly as it was. System
//! awareness rather than per-monitor: crisp on the primary display, where these windows always open
//! (`win32::center_on_screen`), and stretched by Windows if somebody drags one to a monitor with
//! another scale — no `WM_DPICHANGED` handling to get wrong.

use std::cell::Cell;

use windows_sys::Win32::Foundation::{HWND, LPARAM, RECT, SIZE, WPARAM};
use windows_sys::Win32::Graphics::Gdi::{
    AlphaBlend, BeginPaint, BitBlt, CreateCompatibleBitmap, CreateCompatibleDC, CreateDIBSection, CreateFontW, CreateSolidBrush,
    DeleteDC, DeleteObject, DrawTextW, EndPaint, FillRect, GetDC, GetTextExtentPoint32W, InvalidateRect, ReleaseDC, SelectObject,
    SetBkMode, SetTextCharacterExtra, SetTextColor, BITMAPINFO, BITMAPINFOHEADER, BLENDFUNCTION, DIB_RGB_COLORS, DT_CALCRECT,
    DT_CENTER, DT_END_ELLIPSIS, DT_LEFT, DT_NOPREFIX, DT_RIGHT, DT_SINGLELINE, DT_VCENTER, DT_WORDBREAK, HDC, HFONT, PAINTSTRUCT,
    SRCCOPY, TRANSPARENT,
};
use windows_sys::Win32::UI::Controls::{DRAWITEMSTRUCT, ODS_FOCUS, ODS_SELECTED};
use windows_sys::Win32::UI::HiDpi::{GetDpiForSystem, SetThreadDpiAwarenessContext, DPI_AWARENESS_CONTEXT_SYSTEM_AWARE};
use windows_sys::Win32::UI::Input::KeyboardAndMouse::{GetFocus, GetKeyState, SetFocus, VK_RETURN, VK_SHIFT, VK_TAB};
use windows_sys::Win32::UI::WindowsAndMessaging::{
    AdjustWindowRectEx, CreateWindowExW, DefWindowProcW, DestroyIcon, DestroyWindow, DispatchMessageW, GetClientRect, GetDlgCtrlID,
    GetMessageW, GetNextDlgTabItem, GetParent, GetSystemMetrics, GetWindowLongPtrW, KillTimer, MoveWindow, PostQuitMessage,
    SendMessageW, SetForegroundWindow, SetTimer, SetWindowLongPtrW, SetWindowPos, ShowWindow, TranslateMessage, BS_OWNERDRAW,
    GWLP_USERDATA, HICON, ICON_BIG, ICON_SMALL, MSG, SM_CXICON, SM_CXSMICON, SWP_NOACTIVATE, SWP_NOMOVE, SWP_NOZORDER, SW_HIDE, SW_SHOW,
    WM_CLOSE, WM_COMMAND, WM_DESTROY, WM_DRAWITEM, WM_ERASEBKGND, WM_KEYDOWN, WM_NCDESTROY, WM_PAINT, WM_SETFOCUS, WM_SETICON,
    WM_TIMER,
    WS_CAPTION, WS_CHILD, WS_CLIPCHILDREN, WS_EX_TOPMOST, WS_SYSMENU, WS_TABSTOP, WS_VISIBLE,
};

use crate::branding::{self, Branding};
use crate::schedule::now_epoch;
use crate::win32::{center_on_screen, instance, register_class, wide, WindowClass};

// ---------------------------------------------------------------------------------------------
// What a window says. Plain data, so `dialogs` can build and test one without a display.
// ---------------------------------------------------------------------------------------------

/// One window's content, top to bottom. Everything but the title is optional.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Card {
    /// The small capitals above the title: what kind of message this is ("Software updates").
    pub eyebrow: String,
    pub title: String,
    pub body: Option<String>,
    /// A bordered list, one line each — the applications an update will close, for instance.
    pub rows: Vec<Row>,
    pub callout: Option<Callout>,
    pub progress: Option<Progress>,
    /// Small grey text under everything else.
    pub note: Option<String>,
    /// Small grey text in the footer beside a single button.
    pub footer_note: Option<String>,
    /// Left to right. With two, the first sits at the left edge and the second at the right — the
    /// safe choice on the left and never the filled one, as the style guide's dialogs have it.
    pub buttons: Vec<Button>,
    /// Which button has keyboard focus when the window opens, so Return and Space press it. It
    /// carries the focus ring, which is how the safe default shows.
    pub focused: usize,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Row {
    pub label: String,
    pub badge: Option<Badge>,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Badge {
    pub text: String,
    pub tone: Tone,
}

/// The style guide's status colours, as far as anything here uses them.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Tone {
    Info,
    Warning,
}

/// A tinted panel with a coloured rule down its left edge.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Callout {
    pub tone: Tone,
    pub title: Option<String>,
    pub text: String,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Progress {
    pub label: String,
    pub completed: usize,
    pub total: usize,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Button {
    pub label: String,
    pub style: ButtonStyle,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum ButtonStyle {
    /// Filled: the main action. One per window.
    Primary,
    /// White with a coloured border: a positive action that should not invite a reflex click.
    Outline,
    /// White with a grey border: the safe or dismissive choice.
    Neutral,
}

// ---------------------------------------------------------------------------------------------
// The palette and the measurements, in device-independent pixels. The colours are the style
// guide's; the type sizes and spacing are the mockups'.
// ---------------------------------------------------------------------------------------------

pub const ACTION: u32 = 0x0070C0;
const ACTION_PRESSED: u32 = 0x005C9E;
const ACTION_TINT: u32 = 0xE6F3FC;
pub const INK: u32 = 0x0F172A;
pub const SLATE: u32 = 0x475569;
pub const NEUTRAL_50: u32 = 0xF8FAFC;
const NEUTRAL_100: u32 = 0xF1F5F9;
pub const NEUTRAL_200: u32 = 0xE2E8F0;
pub const NEUTRAL_300: u32 = 0xCBD5E1;
pub const WHITE: u32 = 0xFFFFFF;
/// The focus ring. The lighter cyans fail contrast against white.
const FOCUS: u32 = 0x0091D6;
pub const NAVY: u32 = 0x05193A;
pub const CYAN_100: u32 = 0xA7E5FB;

impl Tone {
    /// (text, tint, rule)
    fn colours(self) -> (u32, u32, u32) {
        match self {
            Tone::Info => (0x005270, 0xD8F3FD, 0x00AEEF),
            Tone::Warning => (0x92400E, 0xFEF3C7, 0xF59E0B),
        }
    }
}

const WIDTH: f32 = 480.0;
const PAD_X: f32 = 24.0;
const LOGO_HEIGHT: f32 = 34.0;
/// The widest the header logo may be. A wordmark logo at full height could otherwise push the title
/// into a sliver, or past the window's edge.
const LOGO_MAX_WIDTH: f32 = 160.0;
const BUTTON_HEIGHT: f32 = 38.0;
/// Room round each button for its focus ring: 2px of white, then 2px of ring.
pub const RING: f32 = 4.0;
const FOOTER_PAD: f32 = 16.0;
const BUTTON_ID_BASE: usize = 100;

/// Turns the style guide's `#RRGGBB` into GDI's `0x00BBGGRR`.
pub const fn colorref(rgb: u32) -> u32 {
    ((rgb & 0xFF) << 16) | (rgb & 0xFF00) | ((rgb >> 16) & 0xFF)
}

/// Device pixels per device-independent pixel on this thread.
#[derive(Debug, Clone, Copy)]
pub struct Scale(pub f32);

impl Scale {
    pub fn px(self, dip: f32) -> i32 {
        (dip * self.0).round() as i32
    }

    /// A hairline: never thinner than one device pixel.
    fn line(self) -> i32 {
        self.px(1.0).max(1)
    }
}

/// Opts the calling thread into system DPI awareness — see the module note for why per thread.
/// Must run before the thread creates any window; harmless to repeat.
pub fn enable_dpi_awareness() {
    // SAFETY: takes a constant context and changes only this thread's awareness.
    unsafe {
        SetThreadDpiAwarenessContext(DPI_AWARENESS_CONTEXT_SYSTEM_AWARE);
    }
}

/// The scale to draw at on a thread `enable_dpi_awareness` has run on.
pub fn system_scale() -> Scale {
    // SAFETY: no arguments; answers 96 on a thread that is not DPI aware.
    Scale(unsafe { GetDpiForSystem() }.max(96) as f32 / 96.0)
}

// ---------------------------------------------------------------------------------------------
// Drawing primitives, shared with `session_banner`.
// ---------------------------------------------------------------------------------------------

/// An Arial font, deleted when dropped.
pub struct Font(pub HFONT);

impl Font {
    pub fn new(size: f32, bold: bool, scale: Scale) -> Self {
        let face = wide("Arial");
        // SAFETY: plain values and a live NUL-terminated face name. The literals are, in order,
        // DEFAULT_CHARSET, OUT_DEFAULT_PRECIS, CLIP_DEFAULT_PRECIS, CLEARTYPE_QUALITY and
        // FF_SWISS | DEFAULT_PITCH — written as numbers because windows-sys types each differently.
        let handle = unsafe {
            CreateFontW(-scale.px(size), 0, 0, 0, if bold { 700 } else { 400 }, 0, 0, 0, 1, 0, 0, 5, 0x20, face.as_ptr())
        };
        Font(handle)
    }
}

impl Drop for Font {
    fn drop(&mut self) {
        if !self.0.is_null() {
            // SAFETY: created by CreateFontW above, owned by this value, never selected into a DC
            // past the call that selected it (`text` restores the previous font).
            unsafe {
                DeleteObject(self.0);
            }
        }
    }
}

pub fn rect(left: i32, top: i32, right: i32, bottom: i32) -> RECT {
    RECT { left, top, right, bottom }
}

pub fn inflate(r: RECT, by: i32) -> RECT {
    rect(r.left - by, r.top - by, r.right + by, r.bottom + by)
}

/// # Safety
///
/// `hdc` must be a live device context.
pub unsafe fn fill_rect(hdc: HDC, r: RECT, rgb: u32) {
    let brush = CreateSolidBrush(colorref(rgb));
    FillRect(hdc, &r, brush);
    DeleteObject(brush);
}

/// An antialiased rounded rectangle; a radius of half the height or more makes a pill.
///
/// # Safety
///
/// `hdc` must be a live device context.
pub unsafe fn fill_rounded(hdc: HDC, r: RECT, radius: i32, rgb: u32) {
    let (width, height) = (r.right - r.left, r.bottom - r.top);
    if width <= 0 || height <= 0 {
        return;
    }
    let (red, green, blue) = ((rgb >> 16) as u8, (rgb >> 8) as u8, rgb as u8);
    let premultiply = |channel: u8, alpha: u8| ((channel as u16 * alpha as u16 + 127) / 255) as u8;
    let pixels: Vec<u8> = rounded_rect_alpha(width, height, radius as f32)
        .into_iter()
        .flat_map(|alpha| [premultiply(blue, alpha), premultiply(green, alpha), premultiply(red, alpha), alpha])
        .collect();
    blend(hdc, r.left, r.top, width, height, &pixels);
}

/// How much of each pixel a `width` × `height` rectangle with corners of `radius` covers, row by
/// row from the top, as alpha. Sampled at pixel centres against the shape's signed distance, which
/// gives a one-pixel antialiased edge.
pub fn rounded_rect_alpha(width: i32, height: i32, radius: f32) -> Vec<u8> {
    let (half_width, half_height) = (width as f32 / 2.0, height as f32 / 2.0);
    let radius = radius.clamp(0.0, half_width.min(half_height));
    let mut alpha = Vec::with_capacity((width.max(0) * height.max(0)) as usize);
    for y in 0..height {
        for x in 0..width {
            let qx = (x as f32 + 0.5 - half_width).abs() - (half_width - radius);
            let qy = (y as f32 + 0.5 - half_height).abs() - (half_height - radius);
            let outside = (qx.max(0.0).powi(2) + qy.max(0.0).powi(2)).sqrt();
            let distance = outside + qx.max(qy).min(0.0) - radius;
            alpha.push(((0.5 - distance).clamp(0.0, 1.0) * 255.0).round() as u8);
        }
    }
    alpha
}

/// Composites premultiplied top-down BGRA pixels onto `hdc` at (`x`, `y`).
///
/// # Safety
///
/// `hdc` must be a live device context and `pixels` exactly `width * height * 4` bytes.
pub unsafe fn blend(hdc: HDC, x: i32, y: i32, width: i32, height: i32, pixels: &[u8]) {
    debug_assert_eq!(pixels.len(), (width * height * 4) as usize);
    let memory = CreateCompatibleDC(hdc);
    if memory.is_null() {
        return;
    }
    let mut info: BITMAPINFO = std::mem::zeroed();
    info.bmiHeader.biSize = std::mem::size_of::<BITMAPINFOHEADER>() as u32;
    info.bmiHeader.biWidth = width;
    // Negative: top-down rows, the order the pixels were built in.
    info.bmiHeader.biHeight = -height;
    info.bmiHeader.biPlanes = 1;
    info.bmiHeader.biBitCount = 32;
    let mut bits: *mut core::ffi::c_void = std::ptr::null_mut();
    let bitmap = CreateDIBSection(memory, &info, DIB_RGB_COLORS, &mut bits, std::ptr::null_mut(), 0);
    if !bitmap.is_null() && !bits.is_null() {
        std::ptr::copy_nonoverlapping(pixels.as_ptr(), bits as *mut u8, pixels.len());
        let previous = SelectObject(memory, bitmap);
        // AC_SRC_OVER with per-pixel alpha (AC_SRC_ALPHA = 1).
        let blend = BLENDFUNCTION { BlendOp: 0, BlendFlags: 0, SourceConstantAlpha: 255, AlphaFormat: 1 };
        AlphaBlend(hdc, x, y, width, height, memory, 0, 0, width, height, blend);
        SelectObject(memory, previous);
        DeleteObject(bitmap);
    }
    DeleteDC(memory);
}

/// An image ready to `blend`: premultiplied BGRA, top-down.
pub struct Picture {
    pub width: i32,
    pub height: i32,
    pixels: Vec<u8>,
}

impl Picture {
    /// Scales `image` to `height` device pixels, keeping its shape — or lower, if that would make it
    /// wider than `max_width`.
    pub fn fitted(image: &image::RgbaImage, height: i32, max_width: i32) -> Option<Self> {
        if image.width() == 0 || image.height() == 0 {
            return None;
        }
        let height_for_width = (max_width as i64 * image.height() as i64 / image.width() as i64) as i32;
        Self::at_height(image, height.min(height_for_width).max(1))
    }

    /// Scales `image` to `height` device pixels, keeping its shape.
    fn at_height(image: &image::RgbaImage, height: i32) -> Option<Self> {
        if image.height() == 0 || height <= 0 {
            return None;
        }
        let width = ((image.width() as f32 * height as f32 / image.height() as f32).round() as u32).max(1);
        let scaled = image::imageops::resize(image, width, height as u32, image::imageops::FilterType::Lanczos3);
        let premultiply = |channel: u8, alpha: u8| ((channel as u16 * alpha as u16 + 127) / 255) as u8;
        let pixels = scaled
            .pixels()
            .flat_map(|p| [premultiply(p[2], p[3]), premultiply(p[1], p[3]), premultiply(p[0], p[3]), p[3]])
            .collect();
        Some(Picture { width: width as i32, height, pixels })
    }

    /// # Safety
    ///
    /// `hdc` must be a live device context.
    pub unsafe fn draw(&self, hdc: HDC, x: i32, y: i32) {
        blend(hdc, x, y, self.width, self.height, &self.pixels);
    }
}

/// Draws `text` in `r`, or only measures it when `paint` is false, and answers the height it takes.
/// `spacing` is extra space between letters, for the small capitals.
///
/// # Safety
///
/// `hdc` must be a live device context.
pub unsafe fn text(hdc: HDC, font: &Font, rgb: u32, value: &str, r: RECT, format: u32, spacing: i32, paint: bool) -> i32 {
    if value.is_empty() {
        return 0;
    }
    let previous = SelectObject(hdc, font.0);
    SetTextColor(hdc, colorref(rgb));
    SetBkMode(hdc, TRANSPARENT as i32);
    SetTextCharacterExtra(hdc, spacing);
    let mut encoded: Vec<u16> = value.encode_utf16().collect();
    let mut area = r;
    let format = format | DT_NOPREFIX | if paint { 0 } else { DT_CALCRECT };
    let height = DrawTextW(hdc, encoded.as_mut_ptr(), encoded.len() as i32, &mut area, format);
    SetTextCharacterExtra(hdc, 0);
    SelectObject(hdc, previous);
    height
}

/// How wide `value` is on one line in `font`.
///
/// # Safety
///
/// `hdc` must be a live device context.
pub unsafe fn text_width(hdc: HDC, font: &Font, value: &str) -> i32 {
    let previous = SelectObject(hdc, font.0);
    let encoded: Vec<u16> = value.encode_utf16().collect();
    let mut size = SIZE { cx: 0, cy: 0 };
    GetTextExtentPoint32W(hdc, encoded.as_ptr(), encoded.len() as i32, &mut size);
    SelectObject(hdc, previous);
    size.cx
}

/// Draws one pill button filling `pill`, with its focus ring outside it when `focused`.
///
/// # Safety
///
/// `hdc` must be a live device context.
pub unsafe fn draw_button(hdc: HDC, pill: RECT, style: ButtonStyle, label: &str, font: &Font, focused: bool, pressed: bool, scale: Scale) {
    let radius = (pill.bottom - pill.top) / 2 + scale.px(RING);
    if focused {
        fill_rounded(hdc, inflate(pill, scale.px(RING)), radius, FOCUS);
        fill_rounded(hdc, inflate(pill, scale.px(RING / 2.0)), radius, WHITE);
    }
    let radius = (pill.bottom - pill.top) / 2;
    let border = scale.line();
    let label_colour = match style {
        ButtonStyle::Primary => {
            fill_rounded(hdc, pill, radius, if pressed { ACTION_PRESSED } else { ACTION });
            WHITE
        }
        ButtonStyle::Outline => {
            fill_rounded(hdc, pill, radius, ACTION);
            fill_rounded(hdc, inflate(pill, -border), radius - border, if pressed { ACTION_TINT } else { WHITE });
            if pressed { ACTION_PRESSED } else { ACTION }
        }
        ButtonStyle::Neutral => {
            fill_rounded(hdc, pill, radius, NEUTRAL_300);
            fill_rounded(hdc, inflate(pill, -border), radius - border, if pressed { NEUTRAL_100 } else { WHITE });
            if pressed { INK } else { SLATE }
        }
    };
    text(hdc, font, label_colour, label, pill, DT_CENTER | DT_VCENTER | DT_SINGLELINE, 0, true);
}

// ---------------------------------------------------------------------------------------------
// The window.
// ---------------------------------------------------------------------------------------------

struct Fonts {
    eyebrow: Font,
    title: Font,
    body: Font,
    body_bold: Font,
    small: Font,
    badge: Font,
    button: Font,
}

impl Fonts {
    fn new(scale: Scale) -> Self {
        Fonts {
            eyebrow: Font::new(12.0, true, scale),
            title: Font::new(19.0, true, scale),
            body: Font::new(14.0, false, scale),
            body_bold: Font::new(14.0, true, scale),
            small: Font::new(13.0, false, scale),
            badge: Font::new(12.0, true, scale),
            button: Font::new(14.0, true, scale),
        }
    }
}

/// Everything a window needs to paint itself, owned through `GWLP_USERDATA`.
struct State {
    card: Card,
    scale: Scale,
    fonts: Fonts,
    logo: Option<Picture>,
    icons: Vec<HICON>,
    buttons: Vec<HWND>,
    /// A modal window ends its thread's message loop when answered; a modeless one (the progress
    /// window) hides instead, because its thread is the notification-area icon's.
    modal: bool,
}

impl Drop for State {
    fn drop(&mut self) {
        for icon in &self.icons {
            // SAFETY: created by `tray_menu::icon_from_rgba` for this window alone; WM_SETICON does
            // not take ownership, so they are this value's to destroy once the window has gone.
            unsafe {
                DestroyIcon(*icon);
            }
        }
    }
}

struct Layout {
    height: i32,
    /// Each button's window rectangle: the pill plus room for its ring.
    buttons: Vec<RECT>,
}

impl State {
    /// Lays the card out at the window's width, painting it into `hdc` when `paint` is set and
    /// only measuring otherwise. One function for both, so the window can never be sized for a
    /// layout other than the one it draws.
    unsafe fn layout(&self, hdc: HDC, paint: bool) -> Layout {
        let s = self.scale;
        let f = &self.fonts;
        let card = &self.card;
        let width = s.px(WIDTH);
        let left = s.px(PAD_X);
        let right = width - s.px(PAD_X);
        let tall = 10_000;
        let mut y = s.px(20.0);

        // The header: logo, then the small capitals and the title beside it.
        let text_left = match &self.logo {
            Some(logo) => left + logo.width + s.px(14.0),
            None => left,
        };
        let eyebrow = card.eyebrow.to_uppercase();
        let eyebrow_height = text(hdc, &f.eyebrow, SLATE, &eyebrow, rect(text_left, 0, right, tall), DT_LEFT | DT_WORDBREAK, s.px(0.7), false);
        let title_height = text(hdc, &f.title, INK, &card.title, rect(text_left, 0, right, tall), DT_LEFT | DT_WORDBREAK, 0, false);
        let block = eyebrow_height + s.px(2.0) + title_height;
        let header = block.max(self.logo.as_ref().map_or(0, |logo| logo.height));
        if paint {
            if let Some(logo) = &self.logo {
                logo.draw(hdc, left, y + (header - logo.height) / 2);
            }
            let top = y + (header - block) / 2;
            text(hdc, &f.eyebrow, SLATE, &eyebrow, rect(text_left, top, right, tall), DT_LEFT | DT_WORDBREAK, s.px(0.7), true);
            let top = top + eyebrow_height + s.px(2.0);
            text(hdc, &f.title, INK, &card.title, rect(text_left, top, right, tall), DT_LEFT | DT_WORDBREAK, 0, true);
        }
        y += header;

        if let Some(body) = &card.body {
            y += s.px(14.0);
            y += text(hdc, &f.body, SLATE, body, rect(left, y, right, y + tall), DT_LEFT | DT_WORDBREAK, 0, paint);
        }

        if !card.rows.is_empty() {
            y += s.px(14.0);
            y = self.rows(hdc, left, right, y, paint);
        }

        if let Some(callout) = &card.callout {
            y += s.px(14.0);
            y = self.callout(hdc, callout, left, right, y, paint);
        }

        if let Some(progress) = &card.progress {
            y += s.px(18.0);
            let count = if progress.total > 0 { format!("{} of {}", (progress.completed + 1).min(progress.total), progress.total) } else { String::new() };
            let count_width = if count.is_empty() { 0 } else { text_width(hdc, &f.small, &count) + s.px(12.0) };
            let label_height = text(
                hdc,
                &f.small,
                SLATE,
                &progress.label,
                rect(left, y, right - count_width, y + s.px(40.0)),
                DT_LEFT | DT_SINGLELINE | DT_END_ELLIPSIS,
                0,
                paint,
            );
            text(hdc, &f.small, SLATE, &count, rect(right - count_width, y, right, y + label_height), DT_RIGHT | DT_SINGLELINE, 0, paint);
            y += label_height + s.px(8.0);
            if paint {
                let bar = s.px(8.0);
                fill_rounded(hdc, rect(left, y, right, y + bar), bar / 2, NEUTRAL_100);
                if progress.total > 0 && progress.completed > 0 {
                    let filled = ((right - left) as f32 * progress.completed.min(progress.total) as f32 / progress.total as f32) as i32;
                    fill_rounded(hdc, rect(left, y, left + filled.max(bar), y + bar), bar / 2, ACTION);
                }
            }
            y += s.px(8.0);
        }

        if let Some(note) = &card.note {
            y += s.px(12.0);
            y += text(hdc, &f.small, SLATE, note, rect(left, y, right, y + tall), DT_LEFT | DT_WORDBREAK, 0, paint);
        }

        // The footer: a grey band under a hairline, with the buttons in it.
        y += s.px(20.0);
        let footer_top = y;
        let footer_height = s.px(FOOTER_PAD) * 2 + s.px(BUTTON_HEIGHT);
        if paint {
            fill_rect(hdc, rect(0, footer_top, width, footer_top + footer_height), NEUTRAL_50);
            fill_rect(hdc, rect(0, footer_top, width, footer_top + s.line()), NEUTRAL_200);
        }
        let pill_top = footer_top + s.px(FOOTER_PAD);
        let pills: Vec<i32> =
            card.buttons.iter().map(|button| (text_width(hdc, &f.button, &button.label) + s.px(36.0)).max(s.px(88.0))).collect();
        let mut buttons = vec![rect(0, 0, 0, 0); pills.len()];
        let mut edge = right;
        for index in (0..pills.len()).rev() {
            let pill_left = if index == 0 && pills.len() > 1 { left } else { edge - pills[index] };
            buttons[index] = inflate(rect(pill_left, pill_top, pill_left + pills[index], pill_top + s.px(BUTTON_HEIGHT)), s.px(RING));
            edge = pill_left - s.px(12.0);
        }
        if let (Some(note), Some(first)) = (&card.footer_note, buttons.first()) {
            let note_right = first.left + s.px(RING) - s.px(12.0);
            text(
                hdc,
                &f.small,
                SLATE,
                note,
                rect(left, footer_top, note_right, footer_top + footer_height),
                DT_LEFT | DT_VCENTER | DT_SINGLELINE | DT_END_ELLIPSIS,
                0,
                paint,
            );
        }

        Layout { height: footer_top + footer_height, buttons }
    }

    /// The bordered list. Measured first, because its border has to be drawn under the rows.
    unsafe fn rows(&self, hdc: HDC, left: i32, right: i32, top: i32, paint: bool) -> i32 {
        let s = self.scale;
        let f = &self.fonts;
        let (pad_x, pad_y) = (s.px(14.0), s.px(10.0));
        let measured: Vec<(i32, i32, i32)> = self
            .card
            .rows
            .iter()
            .map(|row| {
                let (badge_width, badge_height) = match &row.badge {
                    Some(badge) => {
                        let label = badge.text.to_uppercase();
                        let height = text(hdc, &f.badge, SLATE, &label, rect(0, 0, 1000, 1000), DT_SINGLELINE, 0, false);
                        (text_width(hdc, &f.badge, &label) + s.px(18.0), height + s.px(8.0))
                    }
                    None => (0, 0),
                };
                let label_right = right - pad_x - if badge_width > 0 { badge_width + s.px(12.0) } else { 0 };
                let label_height = text(hdc, &f.body, INK, &row.label, rect(left + pad_x, 0, label_right, 10_000), DT_LEFT | DT_WORDBREAK, 0, false);
                (label_height.max(badge_height) + pad_y * 2, badge_width, badge_height)
            })
            .collect();
        let bottom = top + measured.iter().map(|(height, _, _)| height).sum::<i32>();
        if !paint {
            return bottom;
        }

        let line = s.line();
        let radius = s.px(8.0);
        fill_rounded(hdc, rect(left, top, right, bottom), radius, NEUTRAL_200);
        fill_rounded(hdc, rect(left + line, top + line, right - line, bottom - line), radius - line, WHITE);
        let mut y = top;
        for (index, (row, (height, badge_width, badge_height))) in self.card.rows.iter().zip(&measured).enumerate() {
            if index > 0 {
                fill_rect(hdc, rect(left + line, y, right - line, y + line), NEUTRAL_200);
            }
            let label_right = right - pad_x - if *badge_width > 0 { badge_width + s.px(12.0) } else { 0 };
            let label_height = text(hdc, &f.body, INK, &row.label, rect(left + pad_x, 0, label_right, 10_000), DT_LEFT | DT_WORDBREAK, 0, false);
            text(hdc, &f.body, INK, &row.label, rect(left + pad_x, y + (height - label_height) / 2, label_right, y + height), DT_LEFT | DT_WORDBREAK, 0, true);
            if let Some(badge) = &row.badge {
                let (colour, tint, _) = badge.tone.colours();
                let badge_top = y + (height - badge_height) / 2;
                let area = rect(right - pad_x - badge_width, badge_top, right - pad_x, badge_top + badge_height);
                fill_rounded(hdc, area, s.px(4.0), tint);
                text(hdc, &f.badge, colour, &badge.text.to_uppercase(), area, DT_CENTER | DT_VCENTER | DT_SINGLELINE, 0, true);
            }
            y += height;
        }
        bottom
    }

    /// The tinted panel: a rule-coloured rounded rectangle with the tint drawn over all but its left
    /// edge, which is how the style guide's banner looks.
    unsafe fn callout(&self, hdc: HDC, callout: &Callout, left: i32, right: i32, top: i32, paint: bool) -> i32 {
        let s = self.scale;
        let f = &self.fonts;
        let rule = s.px(4.0);
        let (inner_left, inner_right) = (left + rule + s.px(16.0), right - s.px(16.0));
        let pad = s.px(14.0);
        let title_height = callout
            .title
            .as_deref()
            .map_or(0, |title| text(hdc, &f.body_bold, INK, title, rect(inner_left, 0, inner_right, 10_000), DT_LEFT | DT_WORDBREAK, 0, false));
        let text_height = text(hdc, &f.body, SLATE, &callout.text, rect(inner_left, 0, inner_right, 10_000), DT_LEFT | DT_WORDBREAK, 0, false);
        let bottom = top + pad * 2 + title_height + text_height;
        if paint {
            let (_, tint, rule_colour) = callout.tone.colours();
            let radius = s.px(8.0);
            fill_rounded(hdc, rect(left, top, right, bottom), radius, rule_colour);
            fill_rounded(hdc, rect(left + rule, top, right, bottom), radius, tint);
            if let Some(title) = &callout.title {
                text(hdc, &f.body_bold, INK, title, rect(inner_left, top + pad, inner_right, bottom), DT_LEFT | DT_WORDBREAK, 0, true);
            }
            text(hdc, &f.body, SLATE, &callout.text, rect(inner_left, top + pad + title_height, inner_right, bottom), DT_LEFT | DT_WORDBREAK, 0, true);
        }
        bottom
    }

    /// Measures the card against the screen's DC.
    unsafe fn measure(&self) -> Layout {
        let screen = GetDC(std::ptr::null_mut());
        let layout = self.layout(screen, false);
        ReleaseDC(std::ptr::null_mut(), screen);
        layout
    }
}

const CLASS_NAME: &str = "KintsugiAgentPopup";
const TIMER_ID: usize = 1;
const STYLE: u32 = WS_CAPTION | WS_SYSMENU | WS_CLIPCHILDREN;

fn popup_class() -> &'static WindowClass {
    use std::sync::OnceLock;
    static CLASS: OnceLock<WindowClass> = OnceLock::new();
    // SAFETY: `popup_wnd_proc` is a valid window procedure for the life of the process. No
    // background brush: WM_PAINT covers the whole client area and WM_ERASEBKGND is swallowed.
    CLASS.get_or_init(|| unsafe { register_class(CLASS_NAME, popup_wnd_proc, std::ptr::null_mut()) })
}

/// Creates a hidden card window on the calling thread, sized to fit and centred on the primary
/// display. `None` if Windows refused the window.
///
/// # Safety
///
/// The calling thread must pump messages for the window for as long as it exists.
unsafe fn create(card: Card, modal: bool, branding: &Branding) -> Option<HWND> {
    enable_dpi_awareness();
    let scale = system_scale();
    let logo = branding.logo.as_ref().and_then(|image| Picture::fitted(image, scale.px(LOGO_HEIGHT), scale.px(LOGO_MAX_WIDTH)));
    let state = Box::new(State { card, scale, fonts: Fonts::new(scale), logo, icons: Vec::new(), buttons: Vec::new(), modal });
    let layout = state.measure();

    let ex_style = if modal { WS_EX_TOPMOST } else { 0 };
    let mut frame = rect(0, 0, scale.px(WIDTH), layout.height);
    AdjustWindowRectEx(&mut frame, STYLE, 0, ex_style);
    let (width, height) = (frame.right - frame.left, frame.bottom - frame.top);

    let title = wide(branding.window_title());
    let window = CreateWindowExW(
        ex_style,
        popup_class().name(),
        title.as_ptr(),
        STYLE,
        0,
        0,
        width,
        height,
        std::ptr::null_mut(),
        std::ptr::null_mut(),
        instance(),
        std::ptr::null(),
    );
    if window.is_null() {
        return None;
    }

    let state = Box::into_raw(state);
    SetWindowLongPtrW(window, GWLP_USERDATA, state as isize);
    let state = &mut *state;

    let button_class = wide("BUTTON");
    for (index, (button, area)) in state.card.buttons.iter().zip(&layout.buttons).enumerate() {
        // The caption is never drawn by Windows (BS_OWNERDRAW) but is what a screen reader reads.
        let caption = wide(&button.label);
        let handle = CreateWindowExW(
            0,
            button_class.as_ptr(),
            caption.as_ptr(),
            WS_CHILD | WS_VISIBLE | WS_TABSTOP | BS_OWNERDRAW as u32,
            area.left,
            area.top,
            area.right - area.left,
            area.bottom - area.top,
            window,
            (BUTTON_ID_BASE + index) as _,
            instance(),
            std::ptr::null(),
        );
        state.buttons.push(handle);
    }

    if let Some(logo) = &branding.logo {
        for (which, metric) in [(ICON_SMALL, SM_CXSMICON), (ICON_BIG, SM_CXICON)] {
            let size = GetSystemMetrics(metric).max(16) as u32;
            let square = branding::fit_square(logo, size);
            if let Ok(icon) = crate::tray_menu::icon_from_rgba(square.as_raw(), size as i32, size as i32, None) {
                SendMessageW(window, WM_SETICON, which as WPARAM, icon as LPARAM);
                state.icons.push(icon);
            }
        }
    }

    center_on_screen(window, width, height);
    Some(window)
}

unsafe fn state_of<'a>(window: HWND) -> Option<&'a mut State> {
    (GetWindowLongPtrW(window, GWLP_USERDATA) as *mut State).as_mut()
}

// The answer and deadline of the modal window on *this* thread — thread-local because the
// scheduler thread asks patching questions while the notification-area thread has its own
// windows, and in the session helper the consent dialog has a thread of its own.
thread_local! {
    static RESULT: Cell<Option<usize>> = const { Cell::new(None) };
    static DEADLINE: Cell<u64> = const { Cell::new(0) };
    /// Whether `run_modal`'s loop is running, so a modal window destroyed by anything else ends it
    /// (see WM_DESTROY) while the one `run_modal` destroys after its loop posts nothing.
    static IN_MODAL_LOOP: Cell<bool> = const { Cell::new(false) };
}

/// How often a modal window compares the wall clock with its deadline, in milliseconds.
///
/// The deadline is the agent's, on the wall clock, rather than a `SetTimer` period: on the other two
/// platforms the dialog program's own timer stops while the machine sleeps (measured on macOS as a
/// one-hour prompt that stood for seventy-one hours), and the delay budget runs from the moment the
/// person is asked, lid open or shut. A five-second poll against `now_epoch` reads the same as the
/// other agents' loops, and a machine resumed past the deadline closes the stale prompt within one.
const DEADLINE_POLL_MS: u32 = 5_000;

/// Shows `card` and blocks until a button is pressed or `timeout_seconds` pass on the wall clock.
/// Answers the pressed button's index, or `None` for a timeout or the window's close box.
pub fn run_modal(card: Card, timeout_seconds: u64, branding: &Branding) -> anyhow::Result<Option<usize>> {
    RESULT.with(|result| result.set(None));
    let focused = card.focused;

    // SAFETY: the window and its children are created, pumped and destroyed on this thread, inside
    // this function.
    unsafe {
        let window = create(card, true, branding).ok_or_else(|| anyhow::anyhow!("could not create the dialog window"))?;
        ShowWindow(window, SW_SHOW);
        // Without this the dialog can open behind whatever the user is working in — for a message
        // that is about to interrupt them, silently appearing behind their work is the same as not
        // appearing.
        SetForegroundWindow(window);
        if let Some(button) = state_of(window).and_then(|state| state.buttons.get(focused).copied()) {
            SetFocus(button);
        }

        DEADLINE.with(|deadline| deadline.set(now_epoch().saturating_add(timeout_seconds)));
        SetTimer(window, TIMER_ID, DEADLINE_POLL_MS, None);

        IN_MODAL_LOOP.with(|running| running.set(true));
        let mut message: MSG = std::mem::zeroed();
        while GetMessageW(&mut message, std::ptr::null_mut(), 0, 0) > 0 {
            if message.message == WM_KEYDOWN && handle_key(window, message.wParam) {
                continue;
            }
            TranslateMessage(&message);
            DispatchMessageW(&message);
        }
        IN_MODAL_LOOP.with(|running| running.set(false));

        KillTimer(window, TIMER_ID);
        DestroyWindow(window);
    }

    Ok(RESULT.with(|result| result.get()))
}

/// Tab and Shift+Tab move between the buttons, Return presses the focused one. Done here rather
/// than with `IsDialogMessage`, which answers Return by sending `IDOK` — control id 1 — whatever
/// has focus, so it would press whichever button happened to own that id. Space needs nothing: a
/// BUTTON presses itself on Space.
unsafe fn handle_key(window: HWND, key: WPARAM) -> bool {
    let focus = GetFocus();
    let ours = !focus.is_null() && GetParent(focus) == window;
    if key == VK_TAB as WPARAM {
        let backwards = GetKeyState(VK_SHIFT as i32) < 0;
        let next = GetNextDlgTabItem(window, focus, i32::from(backwards));
        if !next.is_null() {
            SetFocus(next);
        }
        return true;
    }
    if key == VK_RETURN as WPARAM && ours {
        let id = GetDlgCtrlID(focus) as usize;
        SendMessageW(window, WM_COMMAND, id as WPARAM, focus as LPARAM);
        return true;
    }
    false
}

/// A window that stays up while its content changes — the progress window.
pub struct Modeless(HWND);

impl Modeless {
    /// # Safety
    ///
    /// Must be called on a thread that pumps messages for as long as the window exists.
    pub unsafe fn create(card: Card, branding: &Branding) -> Option<Self> {
        create(card, false, branding).map(Modeless)
    }

    /// Replaces the content, resizing the window if it now needs more or less room.
    ///
    /// # Safety
    ///
    /// Must be called on the thread that created the window.
    pub unsafe fn update(&self, card: Card) {
        let Some(state) = state_of(self.0) else { return };
        if state.card == card {
            return;
        }
        state.card = card;
        let layout = state.measure();

        let mut frame = rect(0, 0, state.scale.px(WIDTH), layout.height);
        AdjustWindowRectEx(&mut frame, STYLE, 0, 0);
        SetWindowPos(self.0, std::ptr::null_mut(), 0, 0, frame.right - frame.left, frame.bottom - frame.top, SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE);
        // Not repainted inside MoveWindow: that would send WM_DRAWITEM, and the window procedure's
        // `state_of` would make a second `&mut State` while `state` here is still live.
        for (button, area) in state.buttons.iter().zip(&layout.buttons) {
            MoveWindow(*button, area.left, area.top, area.right - area.left, area.bottom - area.top, 0);
            InvalidateRect(*button, std::ptr::null(), 0);
        }
        InvalidateRect(self.0, std::ptr::null(), 0);
    }

    /// # Safety
    ///
    /// Must be called on the thread that created the window.
    pub unsafe fn show(&self) {
        ShowWindow(self.0, SW_SHOW);
        SetForegroundWindow(self.0);
    }

    /// # Safety
    ///
    /// Must be called on the thread that created the window.
    pub unsafe fn hide(&self) {
        ShowWindow(self.0, SW_HIDE);
    }
}

unsafe extern "system" fn popup_wnd_proc(window: HWND, message: u32, wparam: WPARAM, lparam: LPARAM) -> isize {
    match message {
        WM_ERASEBKGND => 1,
        WM_PAINT => {
            let mut paint: PAINTSTRUCT = std::mem::zeroed();
            let hdc = BeginPaint(window, &mut paint);
            if let Some(state) = state_of(window) {
                let mut client = rect(0, 0, 0, 0);
                GetClientRect(window, &mut client);
                // Drawn off screen and copied in one go, so the window never shows half a frame.
                let memory = CreateCompatibleDC(hdc);
                let bitmap = CreateCompatibleBitmap(hdc, client.right, client.bottom);
                let previous = SelectObject(memory, bitmap);
                fill_rect(memory, client, WHITE);
                state.layout(memory, true);
                BitBlt(hdc, 0, 0, client.right, client.bottom, memory, 0, 0, SRCCOPY);
                SelectObject(memory, previous);
                DeleteObject(bitmap);
                DeleteDC(memory);
            }
            EndPaint(window, &paint);
            0
        }
        WM_DRAWITEM => {
            let item = &*(lparam as *const DRAWITEMSTRUCT);
            let index = (item.CtlID as usize).wrapping_sub(BUTTON_ID_BASE);
            if let Some(state) = state_of(window) {
                if let Some(button) = state.card.buttons.get(index) {
                    let area = item.rcItem;
                    fill_rect(item.hDC, area, NEUTRAL_50);
                    let pill = inflate(area, -state.scale.px(RING));
                    let focused = item.itemState & ODS_FOCUS != 0;
                    let pressed = item.itemState & ODS_SELECTED != 0;
                    draw_button(item.hDC, pill, button.style, &button.label, &state.fonts.button, focused, pressed, state.scale);
                }
            }
            1
        }
        WM_COMMAND => {
            let index = (wparam & 0xFFFF).wrapping_sub(BUTTON_ID_BASE);
            match state_of(window) {
                Some(state) if index < state.buttons.len() => {
                    if state.modal {
                        RESULT.with(|result| result.set(Some(index)));
                        PostQuitMessage(0);
                    } else {
                        ShowWindow(window, SW_HIDE);
                    }
                    0
                }
                _ => DefWindowProcW(window, message, wparam, lparam),
            }
        }
        WM_TIMER if wparam == TIMER_ID => {
            // A poll, not the timeout: only the wall clock passing the deadline ends the dialog.
            // The answer stays None, which every caller reads as "nobody answered".
            if now_epoch() >= DEADLINE.with(|deadline| deadline.get()) {
                PostQuitMessage(0);
            }
            0
        }
        WM_CLOSE => {
            // The close box. A modal window reads it as no answer (the caller decides what silence
            // means); the progress window just goes out of the way and comes back at the next step.
            match state_of(window) {
                Some(state) if !state.modal => {
                    ShowWindow(window, SW_HIDE);
                    0
                }
                _ => {
                    PostQuitMessage(0);
                    0
                }
            }
        }
        // Only while the loop is running: `run_modal` destroys its window after the loop has
        // already ended, and a quit posted then would sit in the queue and end the next modal loop
        // on this thread before it showed anything. Destroyed any other way, the window's timer
        // goes with it, and without this the loop would wait forever.
        WM_DESTROY => {
            if state_of(window).is_some_and(|state| state.modal) && IN_MODAL_LOOP.with(|running| running.get()) {
                PostQuitMessage(0);
            }
            0
        }
        // Windows gives a re-activated window the focus itself — after Alt-Tab, or a click on its
        // title bar — and then no button has it: no ring, and Return does nothing. Hand it back.
        WM_SETFOCUS => {
            if let Some(button) = state_of(window).and_then(|state| state.buttons.get(state.card.focused).copied()) {
                SetFocus(button);
            }
            0
        }
        WM_NCDESTROY => {
            let state = GetWindowLongPtrW(window, GWLP_USERDATA) as *mut State;
            if !state.is_null() {
                SetWindowLongPtrW(window, GWLP_USERDATA, 0);
                drop(Box::from_raw(state));
            }
            DefWindowProcW(window, message, wparam, lparam)
        }
        _ => DefWindowProcW(window, message, wparam, lparam),
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn colorref_swaps_red_and_blue() {
        assert_eq!(colorref(0x0070C0), 0x00C07000);
    }

    #[test]
    fn a_pill_is_solid_in_the_middle_and_clear_at_its_corners() {
        let alpha = rounded_rect_alpha(40, 10, 5.0);

        assert_eq!(alpha[5 * 40 + 20], 255, "centre");
        assert_eq!(alpha[0], 0, "top-left corner");
        assert_eq!(alpha[40 * 10 - 1], 0, "bottom-right corner");
    }

    #[test]
    fn a_rounded_edge_is_antialiased_rather_than_stepped() {
        // Somewhere along a curved edge a pixel has to be part covered, or the shape is GDI's
        // jagged RoundRect again.
        let alpha = rounded_rect_alpha(40, 40, 20.0);

        assert!(alpha.iter().any(|&a| a > 0 && a < 255));
    }

    #[test]
    fn a_square_corner_covers_every_pixel() {
        assert!(rounded_rect_alpha(6, 4, 0.0).iter().all(|&a| a == 255));
    }

    #[test]
    fn a_radius_larger_than_the_shape_is_clamped_rather_than_inverting_it() {
        let alpha = rounded_rect_alpha(10, 10, 100.0);

        assert_eq!(alpha[5 * 10 + 5], 255);
    }

    /// A wordmark logo is drawn shorter rather than wider than its cap, or the title beside it is
    /// squeezed into a sliver.
    #[test]
    fn a_wide_logo_is_shrunk_to_fit_its_width_cap() {
        let wordmark = image::RgbaImage::new(400, 20);

        let picture = Picture::fitted(&wordmark, 34, 160).unwrap();

        assert_eq!((picture.width, picture.height), (160, 8));
    }

    #[test]
    fn a_square_logo_keeps_the_full_height() {
        let picture = Picture::fitted(&image::RgbaImage::new(64, 64), 34, 160).unwrap();

        assert_eq!((picture.width, picture.height), (34, 34));
    }

    #[test]
    fn scale_rounds_to_whole_device_pixels() {
        assert_eq!(Scale(1.5).px(13.0), 20);
        assert_eq!(Scale(1.0).line(), 1);
        assert_eq!(Scale(0.5).line(), 1);
    }
}
