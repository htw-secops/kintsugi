//! `kintsugi-agent --preview-popups [--organisation NAME] [--logo FILE.png]`: every window a user can
//! be shown — the menu bar popup first — one after another, with sample content, so their look can be checked on a real Mac
//! without waiting for a patch cycle or a remote-control request. It changes nothing: no state is
//! written, nothing is sent to the server or the root daemon, and what is typed into the password
//! prompt is thrown away. As the Windows agent's `preview.rs`.
//!
//! `--organisation` and `--logo` stand in for the server's Patching Policy settings; without them
//! the windows are drawn with whatever this Mac's cached policy carries.

use std::time::Duration;

use anyhow::{Context, Result};

use crate::branding::Branding;
use crate::self_service::Offer;
use crate::tray_popup::{self, InstallState, Panel};
use crate::{config, dialogs, progress_window};

pub fn run(args: &[String]) -> Result<()> {
    let logo = match value_after(args, "--logo") {
        Some(path) => Some(std::fs::read(&path).with_context(|| format!("could not read {path}"))?),
        None => None,
    };
    if logo.is_some() || value_after(args, "--organisation").is_some() {
        Branding::preview_with(value_after(args, "--organisation"), logo);
    }

    let apps: Vec<String> =
        ["Google Chrome", "Slack", "Microsoft Teams", "zoom.us", "Visual Studio Code", "1Password", "Homebrew", "node", "gh", "git", "python@3.13", "awscli"]
            .iter()
            .map(|name| name.to_string())
            .collect();

    println!("1/7  The menu bar popup (press Esc, click elsewhere, or wait 20 seconds)");
    let offer = |name: &str, version: &str| Offer { name: name.into(), latest_version: Some(version.into()) };
    tray_popup::preview(Panel {
        lines: vec!["Next patch due: 2026-11-28 18:00 AEDT".into(), "Status: idle".into(), "Next check-in: 2026-11-27 16:12 AEDT".into()],
        remote_session: Some("IT Support (preview)".into()),
        check_in_enabled: true,
        patch_enabled: true,
        offers: vec![
            (offer("HTW Capture", "0.4.9"), None),
            (offer("Zoom Workplace", "6.2.11"), Some(InstallState::Installing)),
            (offer("Firefox", "145.0"), Some(InstallState::Installed)),
            (offer("7-Zip", "24.08"), Some(InstallState::Failed)),
        ],
    });

    println!("2/7  The update prompt");
    let choice = dialogs::confirm_patch("1 hour", 8, &apps, true, Some("27.2.1"), 600)?;
    println!("     answered: {choice:?}");

    println!("3/7  The notice once every postponement is used");
    dialogs::final_notice(5, &apps, true, 600)?;

    println!("4/7  The progress window (closes by itself)");
    progress_window::show_and_update("Updates start in 5 minutes. Save your work.", 0, 0);
    std::thread::sleep(Duration::from_secs(3));
    for (step, app) in apps.iter().take(4).enumerate() {
        progress_window::show_and_update(app, step, apps.len());
        std::thread::sleep(Duration::from_secs(2));
    }
    progress_window::hide();

    println!("5/7  The remote-control request (nothing connects, whatever you press)");
    let consent = dialogs::confirm_remote_control("IT Support (preview)", &[], 120)?;
    println!("     answered: {consent:?}");

    println!("6/7  The macOS password prompt (type anything: it is thrown away)");
    let username = config::console_username().unwrap_or_else(|| "you".to_string());
    let _ = dialogs::request_install_password(&username, Some("27.2.1"))?;

    println!("7/7  The restart notice");
    dialogs::restart_notice(600)?;

    println!("Done.");
    Ok(())
}

/// The argument after `flag`, if both are there.
fn value_after(args: &[String], flag: &str) -> Option<String> {
    args.iter().position(|arg| arg == flag).and_then(|index| args.get(index + 1)).cloned()
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn value_after_finds_the_word_following_a_flag() {
        let args: Vec<String> = ["--organisation", "Example IT", "--logo"].iter().map(|arg| arg.to_string()).collect();

        assert_eq!(value_after(&args, "--organisation").as_deref(), Some("Example IT"));
        assert_eq!(value_after(&args, "--logo"), None, "a flag with nothing after it");
        assert_eq!(value_after(&args, "--missing"), None);
    }
}
