using Kintsugi.Application.UpgradePaths;

namespace Kintsugi.Tests.Application.UpgradePaths;

/// <summary>
/// Covers the server-written upgrade scripts and the language mapping that decides which
/// interpreter validates and runs them. The scripts themselves aren't executed here — that would
/// need a live network and a real winget/choco — so what's asserted is the contract every consumer
/// depends on: the CLI shape, the absence of any baked-in package name (which is what lets one
/// human signature cover every application), and that a bash script never reaches a Windows bucket.
/// </summary>
public class UpgradeScriptTests
{
    /// <summary>One text per manager — the manager's own row included — so one entry each.</summary>
    public static TheoryData<string> AllScripts() => new()
    {
        HomebrewUpgradeScript.Build(),
        AppStoreUpgradeScript.Build(),
        WingetUpgradeScript.Build(),
        ChocolateyUpgradeScript.Build(),
        FlatpakUpgradeScript.Build(),
        SnapUpgradeScript.Build(),
    };

    [Theory]
    [MemberData(nameof(AllScripts))]
    public void EveryServerWrittenScript_ImplementsTheFullCliContract(string script)
    {
        // The same four tokens AiUpgradePathResearchClient.ValidateScriptAsync requires of an
        // AI-authored script — the agent and the server-side version check invoke both kinds
        // identically, so a server-written one has no licence to differ.
        Assert.Contains("--appName", script);
        Assert.Contains("--appId", script);
        Assert.Contains("--update-version", script);
        Assert.Contains("--update", script);
    }

    [Theory]
    [MemberData(nameof(AllScripts))]
    public void EveryServerWrittenScript_IsIdenticalAcrossApplications(string script)
    {
        // No application name or id is ever baked in — each is read from --appName/--appId at
        // runtime. That's what makes every row of a manager byte-identical, its own row included,
        // so one "Sign Script" review covers them all
        // (see IUpgradePathRepository.FindExistingSignatureForScriptAsync).
        Assert.DoesNotContain("Firefox", script);
        Assert.DoesNotContain("Mozilla", script);
    }

    [Fact]
    public void HomebrewScript_IsBash()
    {
        Assert.StartsWith("#!/bin/bash", HomebrewUpgradeScript.Build());
    }

    [Fact]
    public void HomebrewScript_LooksTheNameUpInLowercaseToo_BecauseTheBrewApiIsCaseSensitive()
    {
        // formulae.brew.sh/api/{formula,cask}/<token>.json is case-sensitive and every brew token is
        // lowercase, but a row's name doesn't have to be: PrepareUpgradePathScanQueryHandler groups
        // an application's variants case-insensitively, so one can settle on the display-cased
        // /Applications bundle name ("Nextcloud") instead of `brew list`'s token ("nextcloud"). Both
        // URLs then 404, LatestVersion stays null, updateAvailable is false and is_patchable is
        // false — the application silently never patches. Asserted here because the failure is
        // invisible: a null LatestVersion is indistinguishable from "no update available".
        var script = HomebrewUpgradeScript.Build();

        Assert.Contains("tr '[:upper:]' '[:lower:]'", script);
        // The name as given is still tried first, so a row already named by its own token is never
        // transformed on the way to a lookup that already worked.
        Assert.Contains("for candidate in \"$APP_NAME\"", script);
    }

    /// <summary>
    /// Homebrew is not a formula, so its own row used to get a second script. It no longer does: the
    /// Applications screen shows the manager's script once, on the "Homebrew" row, on behalf of every
    /// formula and cask nested under it, and that is only honest if the manager's bytes are its
    /// children's bytes. So one script serves every row and tells Homebrew's own apart at runtime by
    /// <c>--appName</c>, which is also what lets one signature cover Homebrew and everything it
    /// manages. See the remarks on <see cref="HomebrewUpgradeScript"/>.
    /// </summary>
    [Fact]
    public void HomebrewScript_TellsItsOwnRowApartAtRuntime_ByTheNameTheAgentReports()
    {
        var script = HomebrewUpgradeScript.Build();

        Assert.Equal(HomebrewUpgradeScript.Build(), script);

        // The branch is on the name the macOS agent reports Homebrew itself under
        // (system_info::HOMEBREW_NAME), compared case-insensitively because rows are matched that
        // way throughout (see UpgradePathRepository.GetAsync).
        Assert.Contains("is_homebrew_itself()", script);
        Assert.Contains("= \"homebrew\"", script);
        // Its version comes from GitHub's releases redirect — the formula API does not know it —
        // and, as CLAUDE.md records, with -fsS rather than -fsSL or the redirect is followed and the
        // variable reporting it is empty.
        Assert.Contains("https://github.com/Homebrew/brew/releases/latest", script);
        Assert.Contains("curl -fsS -o /dev/null -w '%{redirect_url}'", script);
        Assert.DoesNotContain("curl -fsSL -o /dev/null", script);
    }

    [Fact]
    public void HomebrewScript_UpgradesHomebrewItselfOnEveryUpdate_AndOnlyThatOnItsOwnRow()
    {
        var script = HomebrewUpgradeScript.Build();

        // `brew update` is Homebrew's own self-update as well as the index refresh, and it runs
        // unconditionally before the per-application upgrade — so every application upgrade brings
        // Homebrew along, and Homebrew's row needs nothing more.
        var update = script.IndexOf("\nbrew_update\n", StringComparison.Ordinal);
        var upgrade = script.IndexOf("brew upgrade \"$APP_NAME\"", StringComparison.Ordinal);
        Assert.True(update >= 0, "brew update must run on its own line");
        Assert.True(upgrade > update, "brew update must precede the per-application upgrade");

        // Never a blanket `brew upgrade`: that would patch every formula on the host from the
        // manager's row, approved or not. Each application has a row of its own for that.
        Assert.DoesNotContain("brew upgrade\n", script);
        Assert.DoesNotContain("brew update && brew upgrade\n", script);
    }

    [Fact]
    public void HomebrewScript_WaitsOutAnotherBrewUpdate_ButFailsOnAnythingElse()
    {
        var script = HomebrewUpgradeScript.Build();

        // A concurrent `brew update` (the user's own, a `brew autoupdate` job) holds Homebrew's lockf
        // and makes ours fail at once; that is retried, bounded. Any other failure is not.
        Assert.Contains("if output=$(brew update 2>&1); then", script);
        Assert.Contains("*\"process is already running\"*) ;;", script);
        Assert.Contains("*) return 1 ;;", script);
        Assert.Contains("sleep 30", script);
    }

    [Fact]
    public void AppStoreScript_IsBash()
    {
        Assert.StartsWith("#!/bin/bash", AppStoreUpgradeScript.Build());
    }

    /// <summary>
    /// The catalog's entry requirement, asserted at the source: --update-version runs on the API
    /// server, so it may only reach the network — never `mas`, which would need the store session of
    /// a Mac that is not this server. And it has to ask for the right platform: for an app sold as one
    /// purchase on iOS and macOS, <c>entity=macSoftware</c> answers with the iOS record (Pages 15.3
    /// when the Mac build was 15.3.1), so every such row would compare against another platform's
    /// release. <c>desktopSoftware</c> is what the App Store's own tooling asks for.
    /// </summary>
    [Fact]
    public void AppStoreScript_ChecksTheiTunesSearchApiOverHttp_ForTheMacRecordSpecifically()
    {
        var script = AppStoreUpgradeScript.Build();
        var code = CodeLines(script);

        Assert.Contains("https://itunes.apple.com/lookup?bundleId=${APP_ID}&entity=desktopSoftware", code);
        Assert.DoesNotContain("entity=macSoftware", code);
        Assert.DoesNotContain("\nmas ", code);
        // A miss is a 200 with an empty results array, so the count has to be checked or a stray
        // "version" elsewhere in the body would be read as the answer.
        Assert.Contains("\"resultCount\"", code);
    }

    /// <summary>
    /// The script's non-comment lines joined back together. The comments beside these calls exist
    /// precisely to name the flag or entity that must not appear, so an assertion over the whole
    /// text would trip on its own warning label.
    /// </summary>
    private static string CodeLines(string script) =>
        string.Join('\n', script.Split('\n').Where(line => !line.TrimStart().StartsWith('#')));

    /// <summary>
    /// The App Store ships with macOS and is updated by <c>softwareupdate</c>, so its own row has no
    /// answerable version question. One script serves it and every application it manages (see
    /// <see cref="RecognizedPackageManager.BuildScript"/>) and tells the store's row apart at runtime
    /// by <c>--appName</c>, the name the macOS agent reports it under
    /// (<c>system_info::APP_STORE_NAME</c>).
    /// </summary>
    [Fact]
    public void AppStoreScript_TellsItsOwnRowApartAtRuntime_ByTheNameTheAgentReports()
    {
        var script = AppStoreUpgradeScript.Build();

        Assert.Contains("is_app_store_itself()", script);
        Assert.Contains("= \"app store\"", script);
    }

    /// <summary>
    /// --update runs as root from the daemon and reaches the console user's store session through
    /// <c>launchctl asuser</c> — the arrangement the spike proved from a real LaunchDaemon. Every
    /// piece here is load-bearing: <c>SUDO_UID</c> is what makes <c>mas</c> drop to the user for
    /// CommerceKit (bare root is refused outright), <c>--bundle</c> says --appId is a bundle identifier,
    /// and the binary is the agent's own root-owned copy, never Homebrew's.
    /// </summary>
    [Fact]
    public void AppStoreScript_UpdatesAsRoot_InsideTheConsoleUsersSession_WithTheAgentsOwnMas()
    {
        var code = CodeLines(AppStoreUpgradeScript.Build());

        Assert.Contains("launchctl asuser \"$console_uid\"", code);
        Assert.Contains("SUDO_UID=\"$console_uid\"", code);
        Assert.Contains("\"$MAS\" update --verbose --bundle \"$APP_ID\"", code);
        Assert.Contains("MAS=/usr/local/bin/kintsugi-mas", code);
        // A root daemon running a user-writable binary is root for that user, so ownership and mode
        // are checked, not just presence.
        Assert.Contains("stat -f '%u' \"$MAS\"", code);
        Assert.Contains("8#022", code);
        Assert.DoesNotContain("/opt/homebrew/bin/mas", code);
        Assert.DoesNotContain("sudo ", code);
    }

    /// <summary>
    /// A <c>mas update</c> that finds nothing to do exits 0 having printed nothing, and exit 0 is
    /// what makes the agent report the server's latest version as installed
    /// (<c>patch_cycle::run_patches</c>). So an empty result has to be a failure, or the next
    /// inventory contradicts a patch result the agent just sent.
    /// </summary>
    [Fact]
    public void AppStoreScript_TreatsASilentNoOpUpdateAsFailure()
    {
        var script = AppStoreUpgradeScript.Build();
        var updateMode = script.IndexOf("# --update mode", StringComparison.Ordinal);
        Assert.True(updateMode >= 0);
        var tail = CodeLines(script[updateMode..]);

        Assert.Contains("output=$(launchctl asuser", tail);
        Assert.Contains("if [ -z \"$output\" ]; then", tail);
        Assert.DoesNotContain("exit 0", tail);
    }

    [Theory]
    [MemberData(nameof(LinuxScripts))]
    public void LinuxScripts_AreBash(string script)
    {
        Assert.StartsWith("#!/bin/bash", script);
        Assert.DoesNotContain("Invoke-RestMethod", script);
    }

    public static TheoryData<string> LinuxScripts() => new()
    {
        FlatpakUpgradeScript.Build(),
        SnapUpgradeScript.Build(),
    };

    [Fact]
    public void FlatpakScript_ChecksFlathubOverHttp_SoTheVersionCheckCanRunOnTheApiServer()
    {
        var script = FlatpakUpgradeScript.Build();

        // The catalog's entry requirement, asserted at the source: --update-version runs on the API
        // server, so it may only reach the network. A `flatpak remote-info` here would answer about
        // the server rather than the managed host.
        Assert.Contains("flathub.org/api/v2/appstream/", script);
        Assert.DoesNotContain("remote-info", script);
    }

    [Fact]
    public void FlatpakScript_UpdatesTheSystemInstallation_NonInteractively()
    {
        var script = FlatpakUpgradeScript.Build();

        // --user installations belong to one person's home directory and this runs as root; the
        // agent never reports one (see system_info::scan_flatpak), so this must never try.
        Assert.Contains("flatpak update --system", script);
        Assert.Contains("--noninteractive", script);
        Assert.DoesNotContain("flatpak update --user", script);
    }

    /// <summary>
    /// Flatpak is not a Flatpak — it ships as a distribution package — so its own row needs different
    /// handling from every application it manages. One script serves both (see
    /// <see cref="RecognizedPackageManager.BuildScript"/>) and tells Flatpak's row apart at runtime by
    /// <c>--appName</c>, the name the Linux agent reports it under (<c>system_info::FLATPAK_NAME</c>).
    /// </summary>
    [Fact]
    public void FlatpakScript_TellsItsOwnRowApartAtRuntime_ByTheNameTheAgentReports()
    {
        var script = FlatpakUpgradeScript.Build();

        Assert.Contains("is_flatpak_itself()", script);
        Assert.Contains("= \"flatpak\"", script);
        // --appName has to be read for that, where it used to be discarded.
        Assert.Contains("--appName) APP_NAME=\"$2\"", script);
    }

    [Fact]
    public void FlatpakScript_UpgradesFlatpakItselfThroughTheDistributionsPackageManager_AndDoesNotAssumeApt()
    {
        var script = FlatpakUpgradeScript.Build();

        // Flatpak's own row has to work on whichever distribution the host runs.
        foreach (var manager in new[] { "apt-get", "dnf", "zypper", "pacman", "apk" })
        {
            Assert.Contains(manager, script);
        }
        Assert.Contains("DEBIAN_FRONTEND=noninteractive", script);
    }

    /// <summary>
    /// Flatpak's own row must not compare an upstream release against a distribution package.
    /// github.com/flatpak/flatpak publishes 1.18.x while Debian 12 ships 1.14.x, so a row sourcing
    /// its version upstream would read "update available" permanently, and its --update would exit 0
    /// every cycle having changed nothing — a patch that always succeeds and never does anything,
    /// which nothing downstream can tell apart from one that works. Declining to report a version
    /// leaves LatestVersion null, which makes updateAvailable false, which makes the agent skip it;
    /// flatpak is then patched by the OS-update path like any other distribution package.
    /// </summary>
    [Fact]
    public void FlatpakScript_DoesNotClaimAnUpstreamVersionForFlatpakItself_ItCannotActuallyInstall()
    {
        var script = FlatpakUpgradeScript.Build();

        Assert.DoesNotContain("github.com/flatpak/flatpak/releases", script);
        Assert.DoesNotContain("%{redirect_url}", script);
    }

    [Fact]
    public void SnapScript_ChecksTheSnapStoreOverHttp_WithTheHeaderItRequires()
    {
        var script = SnapUpgradeScript.Build();

        Assert.Contains("api.snapcraft.io/v2/snaps/info/", script);
        // The store rejects the request outright without this header, which would make every snap's
        // version check fail and leave LatestVersion null — and a null LatestVersion means nothing
        // on that platform ever patches.
        Assert.Contains("Snap-Device-Series: 16", script);
    }

    [Fact]
    public void SnapScript_PutsSnapBinOnThePath_BecauseASystemdServiceDoesNotHaveIt()
    {
        var script = SnapUpgradeScript.Build();

        Assert.Contains("/snap/bin", script);
        Assert.Contains("snap refresh", script);
    }

    /// <summary>
    /// snapd genuinely is a snap, published under the name "snapd" — the id the Linux agent reports
    /// its own row with — so its refresh is `snap refresh` with a different --appId and nothing more.
    /// Where Homebrew, winget and Flatpak branch at runtime to serve their own row from one text, Snap
    /// and Chocolatey have nothing to branch on.
    /// </summary>
    [Fact]
    public void SnapScript_NeedsNoBranchForSnapdsOwnRow_BecauseSnapdIsItselfASnap()
    {
        var script = SnapUpgradeScript.Build();

        Assert.DoesNotContain("snap refresh snapd", script);
        Assert.DoesNotContain("_itself", script);
    }

    [Theory]
    [InlineData(PackageManagerCatalog.Homebrew, "Homebrew", "firefox")]
    [InlineData(PackageManagerCatalog.AppStore, "App Store", "com.wireguard.macos")]
    [InlineData(PackageManagerCatalog.Winget, "winget", "Mozilla.Firefox")]
    [InlineData(PackageManagerCatalog.Chocolatey, "Chocolatey", "firefox")]
    [InlineData(PackageManagerCatalog.Flatpak, "Flatpak", "org.mozilla.firefox")]
    [InlineData(PackageManagerCatalog.Snap, "Snap", "firefox")]
    public void CurrentScriptFor_TheManagersOwnRow_IsTheSameTextAsAManagedRows(string managerName, string ownRow, string managedRow)
    {
        // How the Upgrade Scripts page notices that a signed row holds a script this build no longer
        // writes — and why a manager's own row and its applications collapse into one entry there.
        // The two share a bucket (a manager is its own manager) and used to be told apart by name;
        // now the *script* does that at runtime and the server writes one text for both, whatever the
        // casing of the row's name.
        Assert.True(PackageManagerCatalog.TryGet(managerName, out var manager));
        var bucket = PlatformBucket.ForPackageManager(managerName);

        Assert.Equal(manager.BuildScript(), PackageManagerCatalog.CurrentScriptFor(managedRow, bucket));
        Assert.Equal(manager.BuildScript(), PackageManagerCatalog.CurrentScriptFor(ownRow, bucket));
        Assert.Equal(manager.BuildScript(), PackageManagerCatalog.CurrentScriptFor(ownRow.ToUpperInvariant(), bucket));
    }

    [Theory]
    [InlineData(PlatformBucket.MacOs)]
    [InlineData(PlatformBucket.Windows)]
    [InlineData(PlatformBucket.Linux)]
    [InlineData(PlatformBucket.Generic)]
    [InlineData("pm:APT")]
    public void CurrentScriptFor_ARowThisServerWritesNoScriptFor_IsNull(string platform)
    {
        // An AI-researched script has no canonical current version to differ from, and neither does
        // an unrecognized manager's row — apt and friends are deliberately absent from the catalog.
        Assert.Null(PackageManagerCatalog.CurrentScriptFor("Firefox", platform));
    }

    [Theory]
    [MemberData(nameof(WindowsScripts))]
    public void WindowsScripts_AreNeverBash(string script)
    {
        // A bash script reaching a Windows host is precisely the failure the per-manager platform
        // bucket exists to prevent; asserting it at the source too means a copy-paste from the
        // Homebrew builder can't reintroduce it silently.
        Assert.DoesNotContain("#!/bin/bash", script);
        Assert.Contains("Set-StrictMode", script);
    }

    public static TheoryData<string> WindowsScripts() => new()
    {
        WingetUpgradeScript.Build(),
        ChocolateyUpgradeScript.Build(),
    };

    [Fact]
    public void WingetScript_UpgradesByExactId_SoAPartialMatchCannotUpgradeADifferentPackage()
    {
        var script = WingetUpgradeScript.Build();

        Assert.Contains("& $winget upgrade --exact --id $AppId --source $source", script);
        // Every flag winget needs to run unattended — without them it blocks on a prompt no one
        // will ever see, and the patch cycle just hangs.
        Assert.Contains("--silent", script);
        Assert.Contains("--accept-package-agreements", script);
        Assert.Contains("--accept-source-agreements", script);
        Assert.Contains("--disable-interactivity", script);
    }

    /// <summary>
    /// The agent runs --update as SYSTEM, which has no <c>winget</c> on PATH (that one is a per-user
    /// App Execution Alias), so the script has to find the packaged binary the way the Windows
    /// agent's <c>system_info::find_packaged_winget</c> does — and must never fall back to the
    /// bare name first, or every winget row fails once the agent can see winget.
    /// </summary>
    [Fact]
    public void WingetScript_RunsThePackagedWinget_BecauseSystemHasNoWingetOnPath()
    {
        var script = WingetUpgradeScript.Build();

        Assert.Contains("function Resolve-Winget", script);
        Assert.Contains("Join-Path $env:ProgramFiles 'WindowsApps'", script);
        Assert.Contains(@"'^Microsoft\.DesktopAppInstaller_(\d+(?:\.\d+)*)_(x64|arm64)__8wekyb3d8bbwe$'", script);
        // A folder only counts when winget.exe is really in it, so a half-removed newest package
        // falls back to the next one rather than to PATH.
        Assert.Contains("Test-Path -LiteralPath $exe -PathType Leaf", script);
        Assert.Contains("$winget = Resolve-Winget", script);
        Assert.Contains("& $winget upgrade --exact --id $AppId --source $source", script);
        // The old PATH-only guard, which is what failed under SYSTEM.
        Assert.DoesNotContain("if (-not (Get-Command winget -ErrorAction SilentlyContinue))", script);
        // The packaged lookup comes before the PATH fallback.
        Assert.True(script.IndexOf("'WindowsApps'", StringComparison.Ordinal) < script.IndexOf("Get-Command winget", StringComparison.Ordinal));
    }

    /// <summary>
    /// winget is not a winget package under its own name — it ships inside App Installer — so its
    /// own row needs a different version source and a different id to upgrade. One script serves it
    /// and every package it manages (see <see cref="RecognizedPackageManager.BuildScript"/>), told
    /// apart at runtime by <c>--appName</c>, the name the Windows agent reports winget's row under
    /// (<c>system_info::WINGET_NAME</c>).
    /// </summary>
    [Fact]
    public void WingetScript_TellsItsOwnRowApartAtRuntime_ByTheNameTheAgentReports()
    {
        var script = WingetUpgradeScript.Build();

        Assert.Contains("function Test-IsWingetItself", script);
        Assert.Contains("$AppName -ieq 'winget'", script);
        Assert.Contains("https://github.com/microsoft/winget-cli/releases/latest", script);
        // winget's own row is repaired, never upgraded with winget (see the self-heal tests below),
        // and that happens before winget is looked for at all.
        Assert.Contains("$repair = @(Invoke-WingetSelfHeal)", script);
        Assert.DoesNotContain("$upgradeId", script);
        Assert.True(
            script.IndexOf("$repair = @(Invoke-WingetSelfHeal)", StringComparison.Ordinal)
                < script.IndexOf("$winget = Resolve-Winget", StringComparison.Ordinal),
            "the repair of winget's own row must not depend on finding a working winget");
        // Still one lookup path for every managed package: the manifest tree of winget-pkgs.
        Assert.Contains("repos/microsoft/winget-pkgs/contents", script);
    }

    /// <summary>
    /// Without <c>--source</c> winget also queries msstore, and an msstore failure then breaks
    /// upgrades that have nothing to do with the Store: on a host whose winget is too old to validate
    /// the current Store certificate, LibreOffice failed with 0x8A15005E.
    /// </summary>
    [Fact]
    public void WingetScript_UpgradesFromTheSourceThatOwnsTheId_AndOnlyAStoreIdGoesToMsstore()
    {
        var script = WingetUpgradeScript.Build();

        // The command takes its source from the id; msstore is never named in an upgrade line itself.
        var upgrades = script.Split('\n').Where(l => l.Contains("upgrade --exact", StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(upgrades);
        Assert.All(upgrades, l => Assert.Contains("--source $source", l));
        Assert.All(upgrades, l => Assert.DoesNotContain("msstore", l));
        Assert.Contains("$source = Get-PackageSource -Id $AppId", script);
        Assert.Contains("return 'winget'", script);
    }

    /// <summary>
    /// An old winget cannot upgrade itself (it cannot validate the certificate it would need), so
    /// winget's own row repairs App Installer from outside winget. Every step that makes that safe is
    /// asserted here, since the script cannot be run in a test.
    /// </summary>
    [Fact]
    public void WingetScript_RepairsAppInstallerFromTheReleaseOnTheOwnRow_AndChecksEverySignatureFirst()
    {
        var script = WingetUpgradeScript.Build();

        // The version that is really installed, and a minimum that is easy to find and edit.
        Assert.Contains("$MinimumAppInstallerVersion = [version] '1.27.0'", script);
        Assert.Contains("Get-AppxPackage -AllUsers -Name Microsoft.DesktopAppInstaller", script);

        // The release tag comes from the releases/latest redirect and its files from the release page, neither of which
        // is the rate-limited API; the API is only a fallback, for a tag. File names are matched by pattern.
        Assert.Contains("https://github.com/microsoft/winget-cli/releases/latest", script);
        Assert.Contains("https://github.com/microsoft/winget-cli/releases/expanded_assets/", script);
        Assert.Contains("https://api.github.com/repos/microsoft/winget-cli/releases/tags/", script);
        Assert.DoesNotContain("https://api.github.com/repos/microsoft/winget-cli/releases/latest", script);
        Assert.Contains(@"\.msixbundle$", script);
        Assert.Contains(@"_Dependencies\.zip$", script);
        Assert.Contains(@"_License1\.xml$", script);
        Assert.DoesNotContain("Microsoft.DesktopAppInstaller_8wekyb3d8bbwe.msixbundle", script);
        // Only the project's own release downloads may be fetched.
        Assert.Contains(@"^https://github\.com/microsoft/winget-cli/releases/download/", script);

        // Every package is checked before anything is installed: valid, and signed by Microsoft.
        var verify = script.IndexOf("Assert-MicrosoftSigned $bundle", StringComparison.Ordinal);
        var install = script.IndexOf("Add-AppxProvisionedPackage -Online -PackagePath $bundle", StringComparison.Ordinal);
        Assert.True(verify >= 0 && install > verify, "the signatures must be checked before the bundle is installed");
        Assert.Contains("foreach ($dependency in $dependencies) { Assert-MicrosoftSigned $dependency.FullName }", script);
        Assert.Contains("Get-AuthenticodeSignature", script);
        Assert.Contains("$signature.Status -ne 'Valid'", script);
        Assert.Contains("O=Microsoft Corporation", script);

        // Machine-wide, dependencies for this architecture first, then the bundle with its licence.
        Assert.Contains("$env:PROCESSOR_ARCHITECTURE", script);
        Assert.Contains("$env:PROCESSOR_ARCHITEW6432", script);
        var dependencies = script.IndexOf("Add-AppxProvisionedPackage -Online -PackagePath $dependency.FullName", StringComparison.Ordinal);
        Assert.True(dependencies >= 0 && dependencies < install, "dependencies are provisioned before the bundle");
        Assert.Contains("-LicensePath $license", script);

        // Success only when the version read back is at or above the minimum, and the temp folder always goes.
        Assert.Contains("$after -ge $MinimumAppInstallerVersion", script);
        Assert.Contains("Remove-Item -LiteralPath $work -Recurse -Force", script);
    }

    /// <summary>
    /// SYSTEM on the affected hosts has no PowerShell Gallery registered, so a fix that needs
    /// <c>Install-Module</c> or the gallery-hosted repair cmdlet fails exactly where it is needed.
    /// </summary>
    [Fact]
    public void WingetScript_NeedsNoPowerShellGallery()
    {
        var script = WingetUpgradeScript.Build();

        Assert.DoesNotContain("Install-Module", script);
        Assert.DoesNotContain("Find-Module", script);
        Assert.DoesNotContain("Repair-WinGetPackageManager", script);
        Assert.DoesNotContain("Install-PackageProvider", script);
    }

    [Fact]
    public void WingetScript_ReportsABlockedNetworkAsItsOwnStatus_NotAsABrokenScript()
    {
        var script = WingetUpgradeScript.Build();

        // The prefix is the contract: it is what the server and a person can search for.
        Assert.Contains("Blocked: network / SSL inspection", script);
        Assert.Contains("function Test-NetworkOrTlsError", script);
        // Used for both the release lookup and the downloads.
        Assert.Contains("Write-Blocked 'reading the latest winget-cli release'", script);
        Assert.Contains("Write-Blocked 'downloading the winget-cli release'", script);
    }

    /// <summary>
    /// The hint a person reads in the script's own error and the one the server shows beside a
    /// stored failure come from <see cref="ExitCodeHints.Known"/>, so they cannot differ.
    /// </summary>
    [Fact]
    public void WingetScript_ExplainsExitCodesInHex_FromTheSameTableTheServerUses()
    {
        var script = WingetUpgradeScript.Build();

        Assert.Contains("'0x8A15005E' = 'msstore certificate pinning failed: outdated winget or SSL inspection'", script);
        Assert.Contains("'0x80070057' = 'Invalid parameter: often an MSIX framework package or an app already current'", script);
        foreach (var known in ExitCodeHints.Known)
        {
            Assert.Contains($"'{ExitCodeHints.ToHex(known.Code)}' = '{known.Hint}'", script);
        }

        Assert.Contains("'0x{0:X8}' -f $Code", script);
        Assert.Contains("winget upgrade exited with $(Get-ExitCodeText $code)", script);
        // The bare decimal is gone from the message.
        Assert.DoesNotContain("winget upgrade exited with $code\"", script);
    }

    [Fact]
    public void HomebrewScript_ReportsTheResultOfTheLastAttempt_AndTrustsTheVersionOnDisk()
    {
        var script = HomebrewUpgradeScript.Build();

        // A bounded retry whose status is the last attempt's, not the first one's.
        Assert.Contains("upgrade_with_retries", script);
        Assert.Contains("HOMEBREW_CURL_RETRIES=3", script);
        Assert.Contains("local attempt=1 max=3", script);
        Assert.Contains("sleep $((attempt * 30))", script);

        // After a failure the installed version decides, compared as numbers.
        Assert.Contains("version_at_least \"$installed\" \"$TARGET\"", script);
        Assert.Contains("reporting success", script);
        Assert.Contains("brew list \"$kind\" --versions", script);
        Assert.Contains("CFBundleShortVersionString", script);
        Assert.Contains("/usr/libexec/PlistBuddy", script);
        // A brew success is never second-guessed: only a failure can be turned into a success.
        Assert.Contains("if [ \"$status\" -eq 0 ]; then\n  exit 0\nfi", script);
    }

    [Fact]
    public void HomebrewScript_LeavesAnAppThatUpdatesItselfAloneWhenItIsAlreadyCurrent()
    {
        var script = HomebrewUpgradeScript.Build();

        Assert.Contains("cask_auto_updates", script);
        Assert.Contains("auto_updates", script);
        var check = script.IndexOf("is already current: the installed app is", StringComparison.Ordinal);
        var update = script.IndexOf("\nbrew_update\n", StringComparison.Ordinal);
        Assert.True(check >= 0 && check < update, "the real version is compared before any brew command that could fail or fight the app's own updater");
    }

    [Fact]
    public void HomebrewScript_NeverWaitsForAPerson_AndClassifiesTheKnownFailures()
    {
        var script = HomebrewUpgradeScript.Build();

        // No prompt can be answered: stdin is closed and sudo's askpass fails at once.
        Assert.Contains("brew upgrade \"$APP_NAME\" </dev/null", script);
        Assert.Contains("NONINTERACTIVE=1", script);
        Assert.Contains("SUDO_ASKPASS", script);
        Assert.Contains("trap 'rm -f \"$ASKPASS\"' EXIT", script);

        Assert.Contains("Stale Caskroom artifact (check App Management permission)", script);
        Assert.Contains("Requires admin privileges", script);
        Assert.Contains("Wrong execution context", script);
        Assert.Contains("*\"already an App at\"*", script);
        Assert.Contains("*\"Running Homebrew as root\"*", script);
    }

    [Fact]
    public void HomebrewScript_IsValidBash()
    {
        var result = Bash(["-n", "/dev/stdin"], HomebrewUpgradeScript.Build());
        if (result is null)
        {
            return; // no bash on this machine: the other tests still pin the text
        }

        Assert.True(result.Value.ExitCode == 0, "bash -n: " + result.Value.Output);
    }

    [Theory]
    [InlineData("157.0.8103.79", "156.0.8078.80", true)] // the self-updated Chrome against Homebrew's stale record
    [InlineData("156.0.8078.80", "157.0.8103.79", false)]
    [InlineData("1.10.0", "1.9.0", true)] // numeric, not text, comparison
    [InlineData("1.9.0", "1.10.0", false)]
    [InlineData("3.50.0", "3.50.0", true)]
    [InlineData("3.50", "3.50.0", true)] // a missing part is zero
    [InlineData("3.50.0", "3.50.1", false)]
    [InlineData("1.2.3_1", "1.2.3", true)] // a formula revision suffix is not a component
    [InlineData("2026.1.3,1234", "2026.1.3", true)] // nor is a cask's ",<revision>"
    [InlineData("009.1", "9.1", true)] // leading zeros are not octal
    [InlineData("unknown", "1.0", false)] // an unreadable version proves nothing
    [InlineData("1.0", "", false)]
    public void HomebrewScript_ComparesVersionsAsNumbers(string installed, string target, bool expected)
    {
        var functions = Between(HomebrewUpgradeScript.Build(), "version_core() {", "# What brew knows about this name as a cask");
        var result = Bash(["-c", $"set -euo pipefail\n{functions}\nif version_at_least \"$1\" \"$2\"; then echo yes; else echo no; fi", "bash", installed, target], null);
        if (result is null)
        {
            return;
        }

        Assert.Equal(0, result.Value.ExitCode);
        Assert.Equal(expected ? "yes" : "no", result.Value.Output.Trim());
    }

    [Theory]
    [InlineData("It seems there is already an App at '/opt/homebrew/Caskroom/google-chrome/156.0.8078.80/Google Chrome.app'.", "Stale Caskroom artifact (check App Management permission)")]
    [InlineData("Error: Running Homebrew as root is extremely dangerous and no longer supported.", "Wrong execution context: Homebrew will not run as root")]
    [InlineData("sudo: a password is required", "Requires admin privileges")]
    [InlineData("sudo: no tty present and no askpass program specified", "Requires admin privileges")]
    [InlineData("curl: (56) Recv failure: Operation timed out", "")] // transient: retried, not classified
    public void HomebrewScript_ClassifiesKnownFailures_AndLeavesTheRestToBeRetried(string output, string expected)
    {
        var functions = Between(HomebrewUpgradeScript.Build(), "classify_failure() {", "UPGRADE_REASON=\"\"");
        var result = Bash(["-c", $"set -euo pipefail\n{functions}\nclassify_failure \"$1\"", "bash", output], null);
        if (result is null)
        {
            return;
        }

        Assert.Equal(0, result.Value.ExitCode);
        Assert.Equal(expected, result.Value.Output.Trim());
    }

    // brew pretty-prints `info --json=v2` over a hundred lines, with "app": [ on one line and the bundle name on the
    // next. The script collapses the newlines before it reads any of it; this is the shape it has to cope with.
    private const string PrettyCaskJson = """
        {
          "casks": [
            {
              "token": "google-chrome",
              "name": ["Google Chrome"],
              "auto_updates": true,
              "artifacts": [
                {
                  "app": [
                    "Google Chrome.app"
                  ]
                },
                {
                  "zap": [
                    {
                      "trash": ["~/Library/Caches/Google/Chrome"]
                    }
                  ]
                }
              ]
            }
          ]
        }
        """;

    [Fact]
    public void HomebrewScript_CollapsesTheJsonBrewPrintsOverManyLines_BeforeReadingIt()
    {
        var script = HomebrewUpgradeScript.Build();
        Assert.Contains("| tr -d '\\n\\r'", script);

        var functions = Between(script, "cask_auto_updates() {", "# CFBundleShortVersionString from the app's Info.plist");
        var result = Bash(["-c",
            "set -euo pipefail\nCASK_JSON=$(printf '%s' \"$1\" | tr -d '\\n\\r')\n" + functions + "\nif cask_auto_updates; then echo \"auto:$(cask_app_name)\"; else echo \"noauto:$(cask_app_name)\"; fi",
            "bash", PrettyCaskJson], null);
        if (result is null)
        {
            return;
        }

        Assert.Equal(0, result.Value.ExitCode);
        Assert.Equal("auto:Google Chrome.app", result.Value.Output.Trim());
    }

    [Fact]
    public void HomebrewScript_WithoutCollapsingTheNewlines_WouldNotFindTheBundle()
    {
        // The failure the review found, kept as a test of the test: with the JSON as brew prints it, the one-line
        // match finds nothing, which is exactly why the collapse has to stay.
        var functions = Between(HomebrewUpgradeScript.Build(), "cask_auto_updates() {", "# CFBundleShortVersionString from the app's Info.plist");
        var result = Bash(["-c", "set -euo pipefail\nCASK_JSON=\"$1\"\n" + functions + "\necho \"app:$(cask_app_name)\"", "bash", PrettyCaskJson], null);
        if (result is null)
        {
            return;
        }

        Assert.Equal("app:", result.Value.Output.Trim());
    }

    [Theory]
    // bundle version, bundle build, cask target, current?
    [InlineData("4.37.1", "170000", "4.37.1,178610", false)] // an older build of the same version is not current
    [InlineData("4.37.1", "178610", "4.37.1,178610", true)]
    [InlineData("4.37.1", "190000", "4.37.1,178610", true)]
    [InlineData("4.38.0", "100", "4.37.1,178610", true)] // a newer version needs no build comparison
    [InlineData("4.36.9", "999999", "4.37.1,178610", false)]
    [InlineData("4.37.1", "", "4.37.1,178610", true)] // no comparable build on the bundle: the version decides
    [InlineData("4.37.1", "170000", "4.37.1", true)] // the target carries no build
    [InlineData("4.37.1", "170000", "157.0.8103.79,abc", false)] // a revision suffix is not a build
    [InlineData("16.92", "", "16.92.24120731", false)] // a short bundle version is behind the cask's long one
    public void HomebrewScript_ComparesTheBuildToo_WhenTheCaskVersionCarriesOne(string bundleVersion, string bundleBuild, string target, bool expected)
    {
        var script = HomebrewUpgradeScript.Build();
        var functions = Between(script, "version_core() {", "# What brew knows about this name as a cask")
            + Between(script, "target_build() {", "# The version on this Mac");
        var result = Bash(["-c",
            "set -euo pipefail\nBV=\"$1\"; BB=\"$2\"\n" + functions
            + "\napp_bundle_version() { printf '%s' \"$BV\"; }\napp_bundle_build() { [ -n \"$BB\" ] && printf '%s' \"$BB\"; }\n"
            + "if bundle_is_current App.app \"$3\"; then echo yes; else echo no; fi",
            "bash", bundleVersion, bundleBuild, target], null);

        if (result is null)
        {
            return;
        }

        Assert.Equal(0, result.Value.ExitCode);
        Assert.Equal(expected ? "yes" : "no", result.Value.Output.Trim());
    }

    // ---- the winget script's functions, run by a real PowerShell (skipped where there is no pwsh) ----

    private const string WingetHarnessPrelude = """
        Set-StrictMode -Version Latest
        $tokens = $null; $errs = $null
        $ast = [System.Management.Automation.Language.Parser]::ParseFile('__SCRIPT__', [ref]$tokens, [ref]$errs)
        foreach ($f in $ast.FindAll({ param($n) $n -is [System.Management.Automation.Language.FunctionDefinitionAst] }, $true)) { . ([scriptblock]::Create($f.Extent.Text)) }
        """;

    /// <summary>Loads every function of the generated winget script into a real pwsh and runs <paramref name="body"/>.
    /// Null when there is no pwsh on this machine.</summary>
    private static (int ExitCode, string Output)? WingetFunctions(string body)
    {
        var dir = Directory.CreateTempSubdirectory("kintsugi-winget-test-");
        try
        {
            var scriptPath = Path.Combine(dir.FullName, "winget.ps1");
            File.WriteAllText(scriptPath, WingetUpgradeScript.Build());
            var harnessPath = Path.Combine(dir.FullName, "harness.ps1");
            File.WriteAllText(harnessPath, WingetHarnessPrelude.Replace("__SCRIPT__", scriptPath, StringComparison.Ordinal) + "\n" + body);

            var info = new System.Diagnostics.ProcessStartInfo("pwsh")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-File", harnessPath })
            {
                info.ArgumentList.Add(argument);
            }

            using var process = System.Diagnostics.Process.Start(info)!;
            var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
            process.WaitForExit();
            return (process.ExitCode, output);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;
        }
        finally
        {
            dir.Delete(true);
        }
    }

    [Theory]
    [InlineData("9NKSQGP7F2NH", "msstore")] // a Microsoft Store product id
    [InlineData("XP8BT8DW290MPQ", "msstore")] // the 14-character form
    [InlineData("9WZDNCRFJ3PS", "msstore")]
    [InlineData("Mozilla.Firefox", "winget")]
    [InlineData("7zip.7zip", "winget")] // starts with a digit, but has the dot every winget-pkgs id has
    [InlineData("Microsoft.VCLibs.140.00", "winget")]
    [InlineData("Google.Chrome", "winget")]
    [InlineData("abcdefghij12", "winget")] // not upper case
    [InlineData("ABCDEFGHIJKL", "winget")] // no digit
    [InlineData("9NKSQGP7", "winget")] // too short
    public void WingetScript_SendsAStoreIdToMsstoreAndEveryOtherIdToWinget(string id, string expected)
    {
        var result = WingetFunctions($"Get-PackageSource -Id '{id}'");
        if (result is null)
        {
            return;
        }

        Assert.Equal(0, result.Value.ExitCode);
        Assert.Equal(expected, result.Value.Output.Trim());
    }

    [Theory]
    // package version, winget version, latest release, expected reason ("" = nothing to repair)
    [InlineData("", "", "", "App Installer is not installed")]
    [InlineData("1.26.5.0", "1.10.390", "1.12.390", "App Installer 1.26.5.0 is below the minimum 1.27.0")]
    [InlineData("1.27.2.0", "1.10.390", "1.12.390", "winget 1.10.390 is behind the latest release 1.12.390")] // the numbering mix-up: package >= minimum, winget behind
    [InlineData("1.27.2.0", "1.10.390", "v1.12.390", "winget 1.10.390 is behind the latest release 1.12.390")] // a tag with its v
    [InlineData("1.27.2.0", "1.12.390", "1.12.390", "")]
    [InlineData("1.27.2.0", "1.13.0", "v1.12.390", "")]
    [InlineData("1.27.2.0", "1.10.390", "", "")] // the latest release could not be read: no proof, no repair (and Invoke-WingetSelfHeal then fails, below)
    [InlineData("1.27.2.0", "", "1.12.390", "")] // winget's version could not be read
    public void WingetScript_RepairsOnlyWhenEitherNumberingProvesItIsBehind(string package, string cli, string latest, string expected)
    {
        static string V(string value) => value.Length == 0 ? "$null" : $"([version]'{value}')";
        var latestArgument = latest.Length == 0 ? "$null" : $"'{latest}'";
        var result = WingetFunctions(
            $"$reason = Get-RepairReason -PackageVersion {V(package)} -CliVersion {V(cli)} -LatestVersion {latestArgument} -Minimum ([version]'1.27.0')\nif ($reason) {{ $reason }} else {{ '' }}");
        if (result is null)
        {
            return;
        }

        Assert.Equal(0, result.Value.ExitCode);
        Assert.Equal(expected, result.Value.Output.Trim());
    }

    [Theory]
    [InlineData("The operation has timed out.", "could not read the latest winget release")]
    [InlineData("The request was aborted: Could not create SSL/TLS secure channel.", "Blocked: network / SSL inspection while reading the latest winget release")]
    public void WingetScript_SelfHeal_FailsWhenItCannotReadTheLatestRelease_InsteadOfReportingNothingToRepair(string message, string expected)
    {
        // The package is at or above the minimum, so only the latest release could say winget is behind. Exiting 0
        // without it left the row Update Available, re-running every cycle.
        var result = WingetFunctions(
            "$MinimumAppInstallerVersion = [version]'1.27.0'\n"
            + "function Get-AppInstallerVersion { [version]'1.27.2.0' }\n"
            + "function Get-WingetCliVersion { [version]'1.10.390' }\n"
            + $"function Get-LatestVersion {{ param($AppId) throw [System.Net.WebException]::new('{message.Replace("'", "''", StringComparison.Ordinal)}') }}\n"
            + "function Get-LatestReleaseTag { throw 'nothing may be downloaded' }\n"
            + "$code = @(Invoke-WingetSelfHeal)\n\"code=$($code[-1])\"");
        if (result is null)
        {
            return;
        }

        Assert.Equal(0, result.Value.ExitCode);
        Assert.Contains(expected, result.Value.Output);
        Assert.Contains("code=1", result.Value.Output);
        Assert.DoesNotContain("nothing to repair", result.Value.Output);
        Assert.DoesNotContain("nothing may be downloaded", result.Value.Output);
    }

    [Theory]
    [InlineData("The operation has timed out.", false)] // a slow GitHub is not SSL inspection
    [InlineData("The request was aborted: Could not create SSL/TLS secure channel.", true)]
    [InlineData("The remote certificate is invalid according to the validation procedure.", true)]
    [InlineData("Proxy authentication required", true)]
    [InlineData("The remote name could not be resolved: 'github.com'", true)]
    public void WingetScript_CallsOnlyRealInspectionBlocked_NotAPlainTimeout(string message, bool blocked)
    {
        var result = WingetFunctions(
            "function Make-Err($m) { try { throw [System.Net.WebException]::new($m) } catch { $_ } }\n"
            + $"Test-NetworkOrTlsError (Make-Err '{message.Replace("'", "''", StringComparison.Ordinal)}')");
        if (result is null)
        {
            return;
        }

        Assert.Equal(0, result.Value.ExitCode);
        Assert.Equal(blocked ? "True" : "False", result.Value.Output.Trim());
    }

    [Fact]
    public void WingetScript_ReadsTheReleaseFilesFromThePage_NotFromTheRateLimitedApi()
    {
        const string page = """
            <a href="/microsoft/winget-cli/releases/download/v1.12.390/Microsoft.DesktopAppInstaller_8wekyb3d8bbwe.msixbundle" rel="nofollow">x</a>
            <a href="/microsoft/winget-cli/releases/download/v1.12.390/DesktopAppInstaller_Dependencies.zip" rel="nofollow">x</a>
            <a href="/microsoft/winget-cli/releases/download/v1.12.390/e53e159d00e04f729cc2180cffd1c02e_License1.xml" rel="nofollow">x</a>
            <a href="/microsoft/winget-cli/archive/refs/tags/v1.12.390.zip">source</a>
            """;
        var result = WingetFunctions(
            "function Invoke-WebRequest { param([switch]$UseBasicParsing, $ErrorAction, $Headers, $Uri) $script:uri = $Uri; [pscustomobject]@{ Content = @'\n" + page + "\n'@ } }\n"
            + "function Invoke-RestMethod { throw 'the API must not be called when the page answers' }\n"
            + "$assets = @(Get-ReleaseAssets -Tag 'v1.12.390')\n"
            + "$assets.Count\n$script:uri\n"
            + "($assets | Where-Object { $_.name -match '_License1\\.xml$' }).name\n"
            + "($assets | Where-Object { $_.name -match '\\.msixbundle$' }).browser_download_url");
        if (result is null)
        {
            return;
        }

        Assert.Equal(0, result.Value.ExitCode);
        var lines = result.Value.Output.Trim().Split('\n').Select(l => l.Trim()).ToArray();
        Assert.Equal("3", lines[0]); // the source archive is not a release file
        Assert.Equal("https://github.com/microsoft/winget-cli/releases/expanded_assets/v1.12.390", lines[1]);
        Assert.Equal("e53e159d00e04f729cc2180cffd1c02e_License1.xml", lines[2]); // a name with a per-release guid, found by pattern
        Assert.Equal("https://github.com/microsoft/winget-cli/releases/download/v1.12.390/Microsoft.DesktopAppInstaller_8wekyb3d8bbwe.msixbundle", lines[3]);
    }

    private static string Between(string text, string from, string to)
    {
        var start = text.IndexOf(from, StringComparison.Ordinal);
        var end = text.IndexOf(to, start, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start, $"could not find '{from}' .. '{to}' in the script");
        return text[start..end];
    }

    /// <summary>Runs bash with the given arguments and, optionally, text on stdin. Null when there is no bash.</summary>
    private static (int ExitCode, string Output)? Bash(string[] arguments, string? stdin)
    {
        try
        {
            var info = new System.Diagnostics.ProcessStartInfo("bash")
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var argument in arguments)
            {
                info.ArgumentList.Add(argument);
            }

            using var process = System.Diagnostics.Process.Start(info)!;
            if (stdin is not null)
            {
                process.StandardInput.Write(stdin);
            }

            process.StandardInput.Close();
            var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
            process.WaitForExit();
            return (process.ExitCode, output);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    [Fact]
    public void ChocolateyScript_UpgradesUnattended_AndAddressesChocolateyItselfByTheSameId()
    {
        var script = ChocolateyUpgradeScript.Build();

        Assert.Contains("choco upgrade $AppId", script);
        Assert.Contains("-y", script);
        // `chocolatey` is an ordinary package on the same feed and the Windows agent reports the
        // manager's own row with that id (system_info::scan_chocolatey), so nothing is hard-coded
        // for it any more — the same script with a different --appId is the whole self-update.
        Assert.DoesNotContain("'chocolatey'", script);
        Assert.DoesNotContain("_itself", script);
    }

    /// <summary>
    /// A regression guard for a bug that shipped and stayed invisible: `%{redirect_url}` reports the
    /// redirect curl did <em>not</em> follow, so combining it with `-L` makes curl follow the
    /// redirect and report an empty string. Every script using the pair returned no version at all,
    /// which meant a null <c>LatestVersion</c>, which meant <c>updateAvailable</c> false, which meant
    /// the agent's <c>is_patchable</c> said no — the row simply never patched, and nothing anywhere
    /// reported an error. Nothing about that failure is visible without running the script.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllScripts))]
    public void NoScript_CombinesTheRedirectUrlTrickWithFollowRedirects(string script)
    {
        // Vacuous for the two PowerShell scripts by design — they express the same trick as
        // `Invoke-WebRequest -MaximumRedirection 0` plus the Location header, which cannot follow
        // the redirect and so cannot have this bug. The theory covers every script anyway so a new
        // bash one is caught the moment it is added to AllScripts.
        var invocations = script
            .Split('\n')
            // Comments are skipped, not because they don't matter but because the ones next to
            // these calls exist precisely to name the flag that must not appear.
            .Where(line => !line.TrimStart().StartsWith('#'))
            .Where(line => line.Contains("%{redirect_url}"));

        foreach (var line in invocations)
        {
            Assert.DoesNotContain("-fsSL", line);
            Assert.DoesNotContain("--location", line);
        }
    }

    [Theory]
    [InlineData(PlatformBucket.MacOs, ScriptLanguage.Bash)]
    [InlineData(PlatformBucket.Linux, ScriptLanguage.Bash)]
    [InlineData(PlatformBucket.Generic, ScriptLanguage.Bash)]
    [InlineData(PlatformBucket.Windows, ScriptLanguage.PowerShell)]
    public void ScriptLanguages_For_MapsAnOsBucketToItsInterpreter(string platform, ScriptLanguage expected)
    {
        Assert.Equal(expected, ScriptLanguages.For(platform));
    }

    [Theory]
    [InlineData(PackageManagerCatalog.Homebrew, ScriptLanguage.Bash)]
    [InlineData(PackageManagerCatalog.AppStore, ScriptLanguage.Bash)]
    [InlineData(PackageManagerCatalog.Winget, ScriptLanguage.PowerShell)]
    [InlineData(PackageManagerCatalog.Chocolatey, ScriptLanguage.PowerShell)]
    [InlineData(PackageManagerCatalog.Flatpak, ScriptLanguage.Bash)]
    [InlineData(PackageManagerCatalog.Snap, ScriptLanguage.Bash)]
    public void ScriptLanguages_For_MapsAPackageManagerBucketToItsInterpreter(string manager, ScriptLanguage expected)
    {
        Assert.Equal(expected, ScriptLanguages.For(PlatformBucket.ForPackageManager(manager)));
    }

    [Fact]
    public void ScriptLanguages_MatchTheCatalogsOwnDeclaredLanguage()
    {
        // The catalog decides which builder writes a manager's script; ScriptLanguages decides which
        // interpreter validates and runs it. These agreeing is not optional — a mismatch means every
        // version check for that manager fails, LatestVersion stays null, and nothing ever patches.
        foreach (var name in new[]
                 {
                     PackageManagerCatalog.Homebrew, PackageManagerCatalog.AppStore, PackageManagerCatalog.Winget,
                     PackageManagerCatalog.Chocolatey, PackageManagerCatalog.Flatpak, PackageManagerCatalog.Snap
                 })
        {
            Assert.True(PackageManagerCatalog.TryGet(name, out var manager));
            Assert.Equal(manager.Language, ScriptLanguages.For(PlatformBucket.ForPackageManager(name)));
        }
    }

    [Fact]
    public void ScriptLanguages_Interpreter_AndExtension_Pair()
    {
        // pwsh -File refuses a file that isn't .ps1, so these two must move together.
        Assert.Equal("pwsh", ScriptLanguage.PowerShell.Interpreter());
        Assert.Equal(".ps1", ScriptLanguage.PowerShell.FileExtension());
        Assert.Equal("bash", ScriptLanguage.Bash.Interpreter());
        Assert.Equal(".sh", ScriptLanguage.Bash.FileExtension());
    }

    [Fact]
    public void PackageManagerCatalog_DoesNotRecognizeAnUnknownManager()
    {
        Assert.False(PackageManagerCatalog.TryGet("SomeNewManager", out _));
        Assert.False(PackageManagerCatalog.TryGet(null, out _));
    }
}
