//! Whose name and logo the windows carry: the organisation an administrator set on the Patching
//! Policy screen, delivered with the policy (`policy::PatchingPolicy::organisation_name`) and read
//! from its cache whenever a window is drawn, so a change reaches every Mac at its next policy
//! refresh with nothing reinstalled. The Windows agent's `branding.rs` is the same idea; keep the
//! limits in step with it and with the server's validator.
//!
//! The logo is decoded here, in Rust, under explicit limits, and handed to `popup.js` re-encoded at
//! the size it is drawn. The script could take the server's bytes as they are, but then a PNG
//! nobody here had looked at would be decoded by ImageIO in a process drawing over the person's
//! screen; this way the only image AppKit ever sees is one this process produced.

use std::io::Cursor;
use std::sync::OnceLock;

use base64::Engine;
use image::{imageops, ImageFormat, RgbaImage};

use crate::config;
use crate::policy::{self, PatchingPolicy};

/// The title bar's text when no organisation has been set.
const DEFAULT_TITLE: &str = "Kintsugi";
/// The server's own limit on the name (`UpdatePatchingPolicyCommandValidator`).
const MAX_NAME_CHARS: usize = 80;
/// The server refuses a larger logo; one arriving anyway is not shown rather than trusted.
const MAX_LOGO_BYTES: usize = 64 * 1024;
const MAX_LOGO_PIXELS_ACROSS: u32 = 1024;
/// The logo's height in `popup.js` is 34 points; twice that keeps it sharp on a Retina display.
const LOGO_PIXELS_HIGH: u32 = 68;
/// And its widest, likewise doubled from the script's LOGO_MAX_WIDTH.
const LOGO_PIXELS_WIDE: u32 = 320;

#[derive(Clone)]
pub struct Branding {
    pub organisation: Option<String>,
    pub logo: Option<RgbaImage>,
}

/// What `--preview-popups` asked to be shown with, in place of the policy's.
static PREVIEW: OnceLock<Branding> = OnceLock::new();

impl Branding {
    /// The branding to draw with now. Read fresh each time — it is a small file, and reading it
    /// here means no window can be drawn with a name the administrator has since changed.
    pub fn current() -> Self {
        if let Some(preview) = PREVIEW.get() {
            return preview.clone();
        }
        let cached = config::user_policy_cache_path().ok().and_then(|path| policy::load_cached(&path));
        Self::from_policy(cached.as_ref())
    }

    /// Used by `preview` only: draw every window with this name and logo rather than the policy's.
    pub fn preview_with(organisation: Option<String>, logo_png: Option<Vec<u8>>) {
        let organisation = organisation.map(|name| name.trim().chars().take(MAX_NAME_CHARS).collect()).filter(|name: &String| !name.is_empty());
        let _ = PREVIEW.set(Branding { organisation, logo: logo_png.and_then(decode_png) });
    }

    fn from_policy(policy: Option<&PatchingPolicy>) -> Self {
        let organisation = policy
            .and_then(|policy| policy.organisation_name.as_deref())
            .map(str::trim)
            .filter(|name| !name.is_empty())
            .map(|name| name.chars().take(MAX_NAME_CHARS).collect());
        let logo = policy.and_then(|policy| policy.organisation_logo_png.as_deref()).and_then(decode_logo);
        Branding { organisation, logo }
    }

    pub fn window_title(&self) -> &str {
        self.organisation.as_deref().unwrap_or(DEFAULT_TITLE)
    }

    /// The logo as `popup.js` takes it: a PNG, at twice the size it is drawn, base64.
    pub fn logo_for_popup(&self) -> Option<String> {
        self.logo_png().map(|bytes| base64::engine::general_purpose::STANDARD.encode(bytes))
    }

    /// The same PNG unencoded, for the menu bar popup, which AppKit draws in this process
    /// (`tray_popup`) — still only an image this process produced, as the module note says.
    pub fn logo_png(&self) -> Option<Vec<u8>> {
        let logo = self.logo.as_ref()?;
        let mut bytes = Vec::new();
        image::DynamicImage::ImageRgba8(fit(logo, LOGO_PIXELS_HIGH, LOGO_PIXELS_WIDE))
            .write_to(&mut Cursor::new(&mut bytes), ImageFormat::Png)
            .ok()?;
        Some(bytes)
    }
}

fn decode_logo(encoded: &str) -> Option<RgbaImage> {
    // Checked before decoding the base64, so an oversized value costs nothing to refuse.
    if encoded.len() > MAX_LOGO_BYTES.div_ceil(3) * 4 + 4 {
        crate::logging::warn("the organisation logo is larger than the server allows, so it is not shown");
        return None;
    }
    let bytes = base64::engine::general_purpose::STANDARD.decode(encoded.trim()).ok()?;
    decode_png(bytes)
}

fn decode_png(bytes: Vec<u8>) -> Option<RgbaImage> {
    if bytes.len() > MAX_LOGO_BYTES {
        crate::logging::warn("the organisation logo is larger than the server allows, so it is not shown");
        return None;
    }

    // PNG only, and never wider or taller than the limit: a small file can still declare an image
    // of any size, and the decoder would allocate for what it declares.
    let mut reader = image::ImageReader::with_format(Cursor::new(bytes), ImageFormat::Png);
    let mut limits = image::Limits::default();
    limits.max_image_width = Some(MAX_LOGO_PIXELS_ACROSS);
    limits.max_image_height = Some(MAX_LOGO_PIXELS_ACROSS);
    reader.limits(limits);

    match reader.decode() {
        Ok(image) => Some(image.into_rgba8()),
        Err(err) => {
            crate::logging::warn(&format!("the organisation logo could not be read, so it is not shown: {err}"));
            None
        }
    }
}

/// Scales `image` to `height` pixels high, keeping its shape, unless that would make it wider than
/// `max_width` — a wordmark — in which case it is fitted to that width instead.
fn fit(image: &RgbaImage, height: u32, max_width: u32) -> RgbaImage {
    let (width, original_height) = image.dimensions();
    let scale = (height as f32 / original_height.max(1) as f32).min(max_width as f32 / width.max(1) as f32);
    let scaled = |side: u32| ((side as f32 * scale).round() as u32).max(1);
    imageops::resize(image, scaled(width), scaled(original_height), imageops::FilterType::Lanczos3)
}

#[cfg(test)]
mod tests {
    use super::*;

    fn png(width: u32, height: u32) -> Vec<u8> {
        let mut bytes = Vec::new();
        image::DynamicImage::ImageRgba8(RgbaImage::from_pixel(width, height, image::Rgba([0, 174, 239, 255])))
            .write_to(&mut Cursor::new(&mut bytes), ImageFormat::Png)
            .unwrap();
        bytes
    }

    fn policy_with(name: Option<&str>, logo: Option<String>) -> PatchingPolicy {
        let mut policy = PatchingPolicy::for_test(24, 2, 3);
        policy.organisation_name = name.map(str::to_string);
        policy.organisation_logo_png = logo;
        policy
    }

    #[test]
    fn no_policy_yet_means_the_defaults() {
        let branding = Branding::from_policy(None);

        assert!(branding.organisation.is_none() && branding.logo.is_none());
        assert_eq!(branding.window_title(), "Kintsugi");
        assert!(branding.logo_for_popup().is_none());
    }

    #[test]
    fn the_organisation_name_titles_the_window() {
        assert_eq!(Branding::from_policy(Some(&policy_with(Some("  Example IT  "), None))).window_title(), "Example IT");
        assert!(Branding::from_policy(Some(&policy_with(Some("   "), None))).organisation.is_none());
    }

    #[test]
    fn an_overlong_name_is_cut_to_the_servers_limit() {
        let branding = Branding::from_policy(Some(&policy_with(Some(&"x".repeat(200)), None)));

        assert_eq!(branding.organisation.unwrap().chars().count(), MAX_NAME_CHARS);
    }

    #[test]
    fn a_png_logo_reaches_the_popup_at_the_size_it_is_drawn() {
        let encoded = base64::engine::general_purpose::STANDARD.encode(png(300, 300));
        let branding = Branding::from_policy(Some(&policy_with(None, Some(encoded))));

        let handed_over = base64::engine::general_purpose::STANDARD.decode(branding.logo_for_popup().unwrap()).unwrap();
        let image = image::load_from_memory_with_format(&handed_over, ImageFormat::Png).unwrap();
        assert_eq!((image.width(), image.height()), (LOGO_PIXELS_HIGH, LOGO_PIXELS_HIGH));
    }

    #[test]
    fn a_wordmark_is_fitted_to_the_widest_the_header_allows() {
        assert_eq!(fit(&RgbaImage::new(1000, 100), LOGO_PIXELS_HIGH, LOGO_PIXELS_WIDE).dimensions(), (LOGO_PIXELS_WIDE, 32));
    }

    #[test]
    fn anything_but_a_png_or_anything_too_large_is_left_off() {
        let not_png = base64::engine::general_purpose::STANDARD.encode(b"GIF89a not a png");
        assert!(Branding::from_policy(Some(&policy_with(None, Some(not_png)))).logo.is_none());

        let oversized = "A".repeat(MAX_LOGO_BYTES * 2);
        assert!(Branding::from_policy(Some(&policy_with(None, Some(oversized)))).logo.is_none());

        assert!(decode_png(png(MAX_LOGO_PIXELS_ACROSS + 1, 1)).is_none());
    }
}
