//! Whose popups these are: the organisation name and logo an administrator set at Settings >
//! Patching Policy, which the service caches with the rest of the policy (`policy::load_cached`).
//!
//! The server, not `config.toml` and not this binary, because the repository is public — nothing
//! in it names a real fleet — and because self-update replaces only the binary, so a value set at
//! install time would never reach a PC that is already installed. Every popup reads this afresh
//! when it opens, so a changed logo shows from the next check-in on.
//!
//! The session helper reads it too, as SYSTEM, for the remote control consent dialog and banner.
//! The cache sits under `%ProgramData%`, so the logo is decoded with limits rather than trusted:
//! a size cap before decoding and a dimension cap during it.

use std::io::Cursor;

use base64::Engine;
use image::{imageops, ImageFormat, RgbaImage};

use crate::config;
use crate::policy::{self, PatchingPolicy};

/// The window title when no organisation is set.
const DEFAULT_TITLE: &str = "Kintsugi";
/// The server's own limits (`PatchingPolicySettings.UpdateBranding`), repeated as a second check.
const MAX_NAME_CHARS: usize = 80;
const MAX_LOGO_BYTES: usize = 64 * 1024;
/// Generous for a logo drawn 34 pixels high, small enough that no PNG can make a SYSTEM process
/// allocate much decoding it.
const MAX_LOGO_PIXELS_ACROSS: u32 = 1024;

pub struct Branding {
    pub organisation: Option<String>,
    pub logo: Option<RgbaImage>,
}

impl Branding {
    /// What the cached policy says now. Nothing cached yet, or nothing set, gives the defaults.
    pub fn current() -> Self {
        Self::from_policy(policy::load_cached(&config::policy_cache_path()).as_ref())
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
}

/// The base64 PNG the server sent, as pixels — or `None` for anything that is not a modest PNG.
/// A logo that will not decode is logged and left off; it is never a reason not to ask.
fn decode_logo(encoded: &str) -> Option<RgbaImage> {
    if encoded.len() > MAX_LOGO_BYTES.div_ceil(3) * 4 + 4 {
        crate::logging::warn("the organisation logo is larger than the server allows, so it is not shown");
        return None;
    }
    let bytes = base64::engine::general_purpose::STANDARD.decode(encoded.trim()).ok()?;

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

/// `image` scaled to fit a `size` × `size` square, centred on transparency — the shape a window
/// icon has to be, whatever shape the logo is.
pub fn fit_square(image: &RgbaImage, size: u32) -> RgbaImage {
    let (width, height) = image.dimensions();
    let longest = width.max(height).max(1) as f32;
    let fit = |side: u32| ((side as f32 * size as f32 / longest).round() as u32).clamp(1, size);
    let (fitted_width, fitted_height) = (fit(width), fit(height));
    let scaled = imageops::resize(image, fitted_width, fitted_height, imageops::FilterType::Lanczos3);
    let mut square = RgbaImage::new(size, size);
    imageops::overlay(&mut square, &scaled, i64::from((size - fitted_width) / 2), i64::from((size - fitted_height) / 2));
    square
}

#[cfg(test)]
mod tests {
    use super::*;

    fn png_base64(width: u32, height: u32) -> String {
        let mut bytes = Vec::new();
        image::DynamicImage::ImageRgba8(RgbaImage::from_pixel(width, height, image::Rgba([0, 174, 239, 255])))
            .write_to(&mut Cursor::new(&mut bytes), ImageFormat::Png)
            .unwrap();
        base64::engine::general_purpose::STANDARD.encode(bytes)
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

        assert!(branding.organisation.is_none());
        assert!(branding.logo.is_none());
        assert_eq!(branding.window_title(), "Kintsugi");
    }

    #[test]
    fn the_organisation_name_titles_the_window() {
        let branding = Branding::from_policy(Some(&policy_with(Some("  Example IT  "), None)));

        assert_eq!(branding.window_title(), "Example IT");
    }

    #[test]
    fn a_blank_name_is_no_name() {
        assert!(Branding::from_policy(Some(&policy_with(Some("   "), None))).organisation.is_none());
    }

    #[test]
    fn an_overlong_name_is_cut_to_the_servers_limit() {
        let long = "x".repeat(200);

        let branding = Branding::from_policy(Some(&policy_with(Some(&long), None)));

        assert_eq!(branding.organisation.unwrap().chars().count(), MAX_NAME_CHARS);
    }

    #[test]
    fn a_png_logo_is_decoded() {
        let branding = Branding::from_policy(Some(&policy_with(None, Some(png_base64(3, 2)))));

        assert_eq!(branding.logo.map(|logo| logo.dimensions()), Some((3, 2)));
    }

    #[test]
    fn anything_but_a_png_is_left_off() {
        let jpeg_ish = base64::engine::general_purpose::STANDARD.encode([0xFF, 0xD8, 0xFF, 0xE0, 0, 0]);

        assert!(decode_logo(&jpeg_ish).is_none());
        assert!(decode_logo("not base64 at all!").is_none());
    }

    #[test]
    fn a_logo_wider_than_the_limit_is_left_off_rather_than_decoded() {
        assert!(decode_logo(&png_base64(MAX_LOGO_PIXELS_ACROSS + 1, 1)).is_none());
    }

    #[test]
    fn fit_square_keeps_a_wide_logos_shape_and_centres_it() {
        let wide = RgbaImage::from_pixel(40, 20, image::Rgba([0, 0, 0, 255]));

        let square = fit_square(&wide, 16);

        assert_eq!(square.dimensions(), (16, 16));
        assert_eq!(square.get_pixel(8, 0)[3], 0, "the band above a wide logo is transparent");
        assert_eq!(square.get_pixel(8, 8)[3], 255, "the logo itself is in the middle");
    }
}
