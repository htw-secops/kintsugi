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

        Assert.Contains("@('upgrade', '--exact', '--id', $AppId, '--source', $source", script);
        Assert.Contains("& $winget @arguments", script);
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
        Assert.Contains("$lines = @(& $winget @arguments)", script);
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
        var upgrades = script.Split('\n').Where(l => l.Contains("'upgrade', '--exact'", StringComparison.Ordinal)).ToList();
        Assert.Single(upgrades);
        Assert.All(upgrades, l => Assert.Contains("'--source', $source", l));
        Assert.All(upgrades, l => Assert.DoesNotContain("msstore", l));
        Assert.Contains("$source = Get-PackageSource -Id $AppId", script);
        Assert.Contains("return 'winget'", script);
    }

    /// <summary>
    /// An old winget cannot upgrade itself, and production hosts do not pull binaries from GitHub: App Installer
    /// comes only from the copy the host's own Kintsugi server serves, every file hash-checked and every package
    /// signature-checked before anything is installed, with the Microsoft Store as the fallback.
    /// </summary>
    [Fact]
    public void WingetScript_InstallsAppInstallerOnlyFromKintsugi_NeverFromGitHub()
    {
        var script = WingetUpgradeScript.Build();

        // The version that is really installed, and a minimum that is easy to find and edit.
        Assert.Contains("$MinimumAppInstallerVersion = [version] '1.27.0'", script);
        Assert.Contains("Get-AppxPackage -AllUsers -Name Microsoft.DesktopAppInstaller", script);

        // Only the release tag is read from GitHub, from the releases/latest redirect; no file is fetched from there.
        Assert.Contains("https://github.com/microsoft/winget-cli/releases/latest", script);
        Assert.DoesNotContain("browser_download_url", script);
        Assert.DoesNotContain("releases/download", script);
        Assert.DoesNotContain("expanded_assets", script);
        Assert.DoesNotContain("api.github.com/repos/microsoft/winget-cli", script);
        Assert.DoesNotContain("Add-AppxPackage ", script);

        // The one file download is from the agent's own server, and each file is hash-checked.
        Assert.Equal(1, script.Split("Invoke-WebRequest", StringSplitOptions.None).Length - 1);
        Assert.Contains("Invoke-WebRequest -UseBasicParsing -ErrorAction Stop -Uri \"$server/api/agent-packages/app-installer/files/", script);
        Assert.Contains("-Uri \"$server/api/agent-packages/app-installer\"", script);
        Assert.Contains("(Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -ine [string] $file.sha256", script);

        // Nothing is provisioned before every package has been checked: the signature checks come first.
        var signed = script.IndexOf("foreach ($dependency in $dependencies) { Assert-MicrosoftSigned", StringComparison.Ordinal);
        var provisioned = script.IndexOf("Add-AppxProvisionedPackage", StringComparison.Ordinal);
        Assert.True(signed > 0 && provisioned > signed, "every package must be signature-checked before anything is provisioned");
        Assert.Contains("'(^|, )O=Microsoft Corporation(,|$)'", script);

        // Kintsugi's copy is tried before the Store, and the Store is still the fallback.
        Assert.True(
            script.IndexOf("if (Install-AppInstallerFromKintsugi -CliVersion $cli)", StringComparison.Ordinal)
            < script.IndexOf("$scan = Invoke-StoreUpdateScan", StringComparison.Ordinal));
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
        // Used for the one thing the script still reaches out for: the latest release tag.
        Assert.Contains("Write-Blocked 'reading the latest winget release'", script);
    }

    /// <summary>
    /// A host whose App Installer is behind asks the Store to deliver it and says what may be stopping
    /// the Store, but still fails: the scan only starts the work, so it is not a patched machine yet.
    /// </summary>
    [Fact]
    public void WingetScript_AsksTheStoreForAppInstaller_ButStaysFailedUntilCurrent()
    {
        var script = WingetUpgradeScript.Build();

        Assert.Contains("-MethodName UpdateScanMethod", script);
        Assert.Contains("$scan = Invoke-StoreUpdateScan", script);
        Assert.Contains("$blockers = @(Get-StoreUpdateBlockers)", script);
        Assert.Contains("this host stays failed until it is on the latest version.\")\n    return 1", script);
        Assert.DoesNotContain("Add-AppxPackage ", script);
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

    /// <summary>
    /// PdfCraft 0.19.0's MSI defaults to a per-user install and refuses to upgrade the copy installed for
    /// every user (0x8A150049). The script retries with ALLUSERS=1 only on that code, and only for a
    /// product Windows Installer holds per-machine, so a working upgrade and an EXE installer are untouched.
    /// </summary>
    [Fact]
    public void WingetScript_RetriesAnMsiThatRefusedWithAllUsers_OnlyForAPerMachineInstall()
    {
        var script = WingetUpgradeScript.Build();

        Assert.Equal(-1978335159, unchecked((int)0x8A150049));
        const string retryGate = "if ($code -eq -1978335159 -and (Test-PerMachineMsi -Name $AppName)) {";
        Assert.Contains(retryGate, script);
        var gate = script.IndexOf(retryGate, StringComparison.Ordinal);
        var retry = script.IndexOf("-Extra @('--custom', 'ALLUSERS=1')", StringComparison.Ordinal);
        Assert.True(gate >= 0 && retry > gate, "the ALLUSERS=1 retry must sit inside the 0x8A150049 gate");
        Assert.Equal(1, script.Split("--custom", StringSplitOptions.None).Length - 1);
        Assert.Contains(@"Installer\UserData\S-1-5-18\Products", script);
    }

    /// <summary>
    /// The script is signed once and run on every Windows host, so a syntax error fails every winget package at
    /// once; and Windows PowerShell 5.1 reads a script in the ANSI code page, so it must stay ASCII.
    /// </summary>
    [Fact]
    public void WingetScript_ParsesAndIsAscii()
    {
        Assert.All(WingetUpgradeScript.Build(), c => Assert.True(c < 128, $"non-ASCII character U+{(int)c:X4} in the winget script"));

        // The harness prelude has already parsed the script into $errs.
        var result = WingetFunctions("\"errors=$(@($errs).Count) $(@($errs) -join '; ')\"");
        if (result is null)
        {
            return;
        }

        Assert.Equal(0, result.Value.ExitCode);
        Assert.Contains("errors=0", result.Value.Output);
    }

    [Theory]
    // config.toml's api_base_url, the agent's environment override, expected server ("" = none)
    [InlineData("https://kintsugi.example.com:8443/", "", "https://kintsugi.example.com:8443")]
    [InlineData("https://kintsugi.example.com:8443", "https://override.example.com", "https://override.example.com")]
    [InlineData("http://kintsugi.example.com:8443", "", "")] // never over plain text
    [InlineData("", "", "")] // no config at all
    public void WingetScript_FindsTheServerTheWayTheAgentDoes(string configured, string environment, string expected)
    {
        var config = configured.Length == 0 ? "" : $"Set-Content -LiteralPath (Join-Path $dir 'config.toml') -Value @('# kintsugi-agent configuration', 'api_base_url = \"{configured}\"', 'enrollment_token = \"\"')";
        var result = WingetFunctions($$"""
            $env:ProgramData = Join-Path ([IO.Path]::GetTempPath()) ([guid]::NewGuid().ToString('N'))
            $dir = Join-Path $env:ProgramData 'Kintsugi/kintsugi-agent'
            New-Item -ItemType Directory -Path $dir -Force | Out-Null
            {{config}}
            $env:PATCHING_AGENT_API_BASE_URL = '{{environment}}'
            "server=$(Get-KintsugiServer)"
            Remove-Item -LiteralPath $env:ProgramData -Recurse -Force
            """);
        if (result is null)
        {
            return;
        }

        Assert.Equal(0, result.Value.ExitCode);
        Assert.Equal($"server={expected}", result.Value.Output.Trim());
    }

    /// <summary>
    /// LibreOffice reached Failed Updates as "winget upgrade exited with 0x8A150049" and nothing more. What the
    /// installer itself said, from winget's output and the installer log, now comes first, the last attempt's
    /// exit code wins, and progress noise and a successful engine run are left out.
    /// </summary>
    [Fact]
    public void WingetScript_PutsTheInstallersOwnErrorInTheFailure()
    {
        var result = WingetFunctions("""
            $installerLog = Join-Path ([IO.Path]::GetTempPath()) ([guid]::NewGuid().ToString('N') + '.log')
            Set-Content -LiteralPath $installerLog -Encoding Unicode -Value @(
                '=== Logging started: 25/11/2026  08:37:51 ===',
                'Action start 8:37:59: InstallFiles.',
                'Error 1925. You do not have sufficient privileges to complete this installation for all users of the machine.',
                'Action ended 8:38:01: InstallFinalize. Return value 3.',
                'MainEngineThread is returning 0',
                'MainEngineThread is returning 1603')
            $script:WingetOutput = @(
                'Found LibreOffice [TheDocumentFoundation.LibreOffice] Version 26.8.3.1',
                '  ██████████████████████████████   352 MB /  352 MB',
                'Installer failed with exit code: 1618',
                'Starting package install...',
                'Installer failed with exit code: 1603')
            (Get-InstallerEvidence) -join "`n"
            "code=$(Get-InstallerExitCode)"
            Remove-Item -LiteralPath $installerLog -Force
            """);
        if (result is null)
        {
            return;
        }

        Assert.Equal(0, result.Value.ExitCode);
        var output = result.Value.Output;
        Assert.Contains("Installer failed with exit code: 1603", output);
        Assert.Contains("Error 1925. You do not have sufficient privileges", output);
        Assert.Contains("InstallFinalize. Return value 3.", output);
        Assert.Contains("MainEngineThread is returning 1603", output);
        Assert.DoesNotContain("returning 0", output);
        Assert.DoesNotContain("352 MB", output);
        Assert.EndsWith("code=1603", output.Trim());
    }

    // Stand-ins for everything Repair-MissingMsiCache touches on a real host: winget (upgrade and download), the
    // uninstall-key read, the signature, the MSI's ProductCode and msiexec. Each test then changes one thing.
    private const string MissingMsiCacheHarness = """
        $AppName = 'LibreOffice 7.6.7.2'
        $AppId = 'TheDocumentFoundation.LibreOffice'
        $source = 'winget'
        $installerLog = Join-Path ([IO.Path]::GetTempPath()) ([guid]::NewGuid().ToString('N') + '.log')
        Set-Content -LiteralPath $installerLog -Encoding Unicode -Value @(
            'MSI (s) (A4:2C) [08:38:00:120]: Product: LibreOffice 26.8.3.1 -- Error 1714. The older version of LibreOffice 26.8.3.1 cannot be removed. Contact your technical support group.  System Error 1612.',
            'MainEngineThread is returning 1603')
        $script:WingetOutput = @('Installer failed with exit code: 1603')
        $script:WingetLastExtra = @()
        $script:installed = @([pscustomobject]@{ ProductCode = '{39F7F98F-3A8C-43B4-B6C3-778FB442B040}'; DisplayVersion = '7.6.7.2' })
        function Get-InstalledMsiEntry { param([string] $Name) $script:installed }
        $script:downloadedName = 'LibreOffice_7.6.7.2_Win_x86-64.msi'
        $script:downloads = @()
        $script:downloadDir = $null
        function FakeWinget {
            if ($args[0] -eq 'download') {
                $script:downloads += ($args -join ' ')
                $script:downloadDir = $args[[array]::IndexOf($args, '--download-directory') + 1]
                Set-Content -LiteralPath (Join-Path $script:downloadDir $script:downloadedName) -Value 'msi'
                $global:LASTEXITCODE = 0
                'Installer downloaded'
            }
        }
        $winget = 'FakeWinget'
        $script:upgrades = 0
        function Invoke-WingetUpgrade { param([string[]] $Extra) $script:upgrades++; 0 }
        $script:signatureStatus = 'Valid'
        function Get-AuthenticodeSignature { [CmdletBinding()] param([string] $FilePath) [pscustomobject]@{ Status = $script:signatureStatus } }
        $script:msiProductCode = '{39f7f98f-3a8c-43b4-b6c3-778fb442b040}'
        function Get-MsiProductCode { param([string] $Path) $script:msiProductCode }
        $script:msiexec = @()
        function Invoke-Msiexec { param([string[]] $Arguments) $script:msiexec += ($Arguments -join ' '); 0 }
        """;

    /// <summary>
    /// LibreOffice 7.6.7.2 could not be upgraded because Windows Installer's cached copy of its MSI was gone (Error
    /// 1714, System Error 1612), so the major upgrade could never remove it. The script downloads that same version
    /// with winget, re-caches it with msiexec /fv only when its signature is valid and its ProductCode is the
    /// installed one, and then retries the upgrade once. Anything short of that re-caches nothing and retries nothing.
    /// </summary>
    [Theory]
    // what goes wrong, expected in the output
    [InlineData("", "code=0 upgrades=1 downloads=1 msiexec=/fv")]
    [InlineData("$script:msiProductCode = '{00000000-0000-0000-0000-000000000000}'", "is product '{00000000-0000-0000-0000-000000000000}', not the installed")]
    [InlineData("$script:signatureStatus = 'HashMismatch'", "has no valid signature (status HashMismatch)")]
    [InlineData("$script:downloadedName = 'LibreOffice_7.6.7.2_Win_x86-64.exe'", "holds no .msi")]
    [InlineData("$script:downloadedName = 'LibreOffice_7.6.7.2.msix'", "holds no .msi")]
    [InlineData("$script:installed = @()", "0 Windows Installer products are installed under the name")]
    [InlineData("$source = 'msstore'", "is not a winget-source package")]
    [InlineData("Set-Content -LiteralPath $installerLog -Value 'Error 1714. The older version cannot be removed.'", "code=1603 upgrades=0 downloads=0 msiexec= ")]
    [InlineData("Remove-Item -LiteralPath $installerLog", "code=1603 upgrades=0 downloads=0 msiexec= ")]
    public void WingetScript_RecachesAMissingOldMsiOnlyWhenItChecksOut_ThenRetriesOnce(string mischief, string expected)
    {
        var result = WingetFunctions($$"""
            {{MissingMsiCacheHarness}}
            {{mischief}}
            $code = Invoke-UpgradeAfterMsiRecache -Code 1603
            "code=$code upgrades=$($script:upgrades) downloads=$($script:downloads.Count) msiexec=$($script:msiexec -join '|') "
            "leftover=$(if ($script:downloadDir) { Test-Path -LiteralPath $script:downloadDir } else { 'none' })"
            "download=$($script:downloads -join '|')"
            Remove-Item -LiteralPath $installerLog -Force -ErrorAction SilentlyContinue
            """);
        if (result is null)
        {
            return;
        }

        Assert.Equal(0, result.Value.ExitCode);
        var output = result.Value.Output;
        Assert.Contains(expected, output);
        Assert.DoesNotContain("leftover=True", output);
        if (expected.StartsWith("code=0", StringComparison.Ordinal))
        {
            Assert.Contains(@"msiexec=/fv """, output);
            Assert.Contains(@"LibreOffice_7.6.7.2_Win_x86-64.msi"" /qn /norestart ", output);
            Assert.Contains("download=download --exact --id TheDocumentFoundation.LibreOffice --version 7.6.7.2 --source winget --download-directory ", output);
            Assert.Contains("leftover=False", output);
        }
        else
        {
            Assert.Contains("upgrades=0", output);
            Assert.Contains("msiexec= ", output);
        }
    }

    /// <summary>
    /// A dual-scope MSI refused the plain upgrade, the per-machine retry ran with ALLUSERS=1 and then hit Error 1714 /
    /// System Error 1612. The retry after the re-cache must carry ALLUSERS=1 again, or it is refused like the first one.
    /// The real Invoke-WingetUpgrade runs here, against a stand-in winget.
    /// </summary>
    [Fact]
    public void WingetScript_RetryAfterRecache_KeepsTheAllUsersArgumentOfTheFailedAttempt()
    {
        var result = WingetFunctions("""
            {{MissingMsiCacheHarness}}
            foreach ($f in $ast.FindAll({ param($n) $n -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq 'Invoke-WingetUpgrade' }, $true)) { . ([scriptblock]::Create($f.Extent.Text)) }
            $script:WingetOutput = @()
            $script:upgradeCalls = @()
            function FakeWinget {
                if ($args[0] -eq 'download') {
                    $script:downloads += ($args -join ' ')
                    $script:downloadDir = $args[[array]::IndexOf($args, '--download-directory') + 1]
                    Set-Content -LiteralPath (Join-Path $script:downloadDir $script:downloadedName) -Value 'msi'
                    $global:LASTEXITCODE = 0
                    'Installer downloaded'
                    return
                }
                $script:upgradeCalls += ($args -join ' ')
                switch ($script:upgradeCalls.Count) {
                    1 { $global:LASTEXITCODE = -1978335159; 'Installer failed with exit code: 1603' }
                    2 { $global:LASTEXITCODE = -1978335159; 'Installer failed with exit code: 1603' }
                    default { $global:LASTEXITCODE = 0; 'Successfully installed' }
                }
            }
            $code = Invoke-WingetUpgrade
            $code = Invoke-WingetUpgrade -Extra @('--custom', 'ALLUSERS=1')
            $code = Invoke-UpgradeAfterMsiRecache -Code $code
            "code=$code calls=$($script:upgradeCalls.Count) msiexec=$($script:msiexec.Count)"
            for ($i = 0; $i -lt $script:upgradeCalls.Count; $i++) { "call$($i + 1)=$($script:upgradeCalls[$i])" }
            Remove-Item -LiteralPath $installerLog -Force -ErrorAction SilentlyContinue
            """.Replace("{{MissingMsiCacheHarness}}", MissingMsiCacheHarness, StringComparison.Ordinal));
        if (result is null)
        {
            return;
        }

        Assert.Equal(0, result.Value.ExitCode);
        var output = result.Value.Output;
        Assert.Contains("code=0 calls=3 msiexec=1", output);
        Assert.DoesNotContain("--custom", Between(output, "call1=", "\n"));
        Assert.Contains("--custom ALLUSERS=1", Between(output, "call2=", "\n"));
        Assert.Contains("--custom ALLUSERS=1", Between(output, "call3=", "\n"));
    }

    /// <summary>
    /// The re-cache is the only repair: the script never asks msiexec or winget to remove anything, and msiexec is run
    /// in exactly one place, with /fv.
    /// </summary>
    [Fact]
    public void WingetScript_RecacheNeverUninstalls()
    {
        var script = WingetUpgradeScript.Build();
        var msiexecCalls = script.Split("Invoke-Msiexec -Arguments", StringSplitOptions.None).Length - 1;
        Assert.Equal(1, msiexecCalls);
        Assert.Contains("Invoke-Msiexec -Arguments @('/fv', ", script);
        Assert.DoesNotContain("'/x'", script);
        Assert.DoesNotContain("Remove-ItemProperty", script);
        Assert.Contains("$code = Invoke-UpgradeAfterMsiRecache -Code $code", script);
    }

    private const string AppInstallerOfferHarness = """
        $src = Join-Path ([IO.Path]::GetTempPath()) ([guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Path (Join-Path $src 'x64') -Force | Out-Null
        Set-Content -LiteralPath (Join-Path $src 'x64/Microsoft.VCLibs.140.00.UWPDesktop_x64.appx') -Value 'dependency'
        Compress-Archive -Path (Join-Path $src 'x64') -DestinationPath (Join-Path $src 'DesktopAppInstaller_Dependencies.zip')
        Set-Content -LiteralPath (Join-Path $src 'Microsoft.DesktopAppInstaller_8wekyb3d8bbwe.msixbundle') -Value 'bundle'
        Set-Content -LiteralPath (Join-Path $src 'e53e159d_License1.xml') -Value '<License />'
        function Hash($name) { (Get-FileHash -LiteralPath (Join-Path $src $name) -Algorithm SHA256).Hash.ToLowerInvariant() }
        $offer = [pscustomobject]@{ version = 'v1.29.380'; files = @(
            [pscustomobject]@{ role = 'bundle'; name = 'Microsoft.DesktopAppInstaller_8wekyb3d8bbwe.msixbundle'; sha256 = (Hash 'Microsoft.DesktopAppInstaller_8wekyb3d8bbwe.msixbundle') },
            [pscustomobject]@{ role = 'dependencies'; name = 'DesktopAppInstaller_Dependencies.zip'; sha256 = (Hash 'DesktopAppInstaller_Dependencies.zip') },
            [pscustomobject]@{ role = 'license'; name = 'e53e159d_License1.xml'; sha256 = (Hash 'e53e159d_License1.xml') }) }
        function Get-KintsugiServer { 'https://kintsugi.example.com:8443' }
        function Invoke-RestMethod { [CmdletBinding()] param([switch] $UseBasicParsing, [string] $Uri) $offer }
        function Invoke-WebRequest { [CmdletBinding()] param([switch] $UseBasicParsing, [string] $Uri, [string] $OutFile)
            Copy-Item -LiteralPath (Join-Path $src ([uri]::UnescapeDataString(($Uri -split '/')[-1]))) -Destination $OutFile }
        $script:signedBy = 'CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond, S=Washington, C=US'
        function Get-AuthenticodeSignature { [CmdletBinding()] param([string] $FilePath)
            [pscustomobject]@{ Status = 'Valid'; SignerCertificate = [pscustomobject]@{ Subject = $script:signedBy } } }
        $script:provisioned = @()
        function Add-AppxProvisionedPackage { [CmdletBinding()] param([switch] $Online, [string] $PackagePath, [string] $LicensePath, [switch] $SkipLicense)
            $script:provisioned += [IO.Path]::GetFileName($PackagePath) }
        """;

    [Theory]
    // what goes wrong, host's winget version, expected in the output
    [InlineData("", "1.10.390", "result=1.29.380 provisioned=Microsoft.VCLibs.140.00.UWPDesktop_x64.appx,Microsoft.DesktopAppInstaller_8wekyb3d8bbwe.msixbundle")]
    [InlineData("$offer.files[0].sha256 = '00'", "1.10.390", "does not match the SHA-256 Kintsugi lists for it")]
    [InlineData("$script:signedBy = 'CN=Someone Else, O=Someone Else'", "1.10.390", "is signed, but not by Microsoft Corporation")]
    [InlineData("$offer.files[1].name = '../escape.zip'", "1.10.390", "names an unexpected file")]
    [InlineData("", "1.29.380", "no newer than this host's 1.29.380")]
    public void WingetScript_InstallsKintsugisAppInstallerOnlyWhenEveryFileChecksOut(string mischief, string hostWinget, string expected)
    {
        var result = WingetFunctions($$"""
            {{AppInstallerOfferHarness}}
            {{mischief}}
            $result = Install-AppInstallerFromKintsugi -CliVersion ([version]'{{hostWinget}}')
            "result=$result provisioned=$($script:provisioned -join ',')"
            Remove-Item -LiteralPath $src -Recurse -Force
            """);
        if (result is null)
        {
            return;
        }

        Assert.Equal(0, result.Value.ExitCode);
        Assert.Contains(expected, result.Value.Output);
        if (!expected.StartsWith("result=1.29.380", StringComparison.Ordinal))
        {
            Assert.Contains("result= provisioned=", result.Value.Output);
        }
    }

    [Theory]
    // Product codes and their packed form as they were read off a real host's registry.
    [InlineData("{39F7F98F-3A8C-43B4-B6C3-778FB442B040}", "F89F7F93C8A34B346B3C77F84B240B04")]
    [InlineData("{6A470C74-8F7F-44AA-B3D6-F0781D4C8351}", "47C074A6F7F8AA443B6D0F87D1C43815")]
    [InlineData("{a054eca7-c4c2-405b-8cb3-253e5a4c5609}", "7ACE450A2C4CB504C83B52E3A5C46590")]
    [InlineData("Mozilla Firefox 154.0 (x64 en-US)", "")]
    [InlineData("39F7F98F-3A8C-43B4-B6C3-778FB442B040", "")] // no braces: not an MSI product key
    public void WingetScript_PacksAProductCodeTheWayWindowsInstallerStoresIt(string productCode, string expected)
    {
        var result = WingetFunctions($"ConvertTo-PackedProductCode '{productCode}'");
        if (result is null)
        {
            return;
        }

        Assert.Equal(0, result.Value.ExitCode);
        Assert.Equal(expected, result.Value.Output.Trim());
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

    /// <summary>
    /// What the blocker list names on a whole fleet that carries RemoveWindowsStore=1: a host whose Store app was removed
    /// is told so, and the policy is named only where Windows honours it. On Pro it was the red herring every stuck host
    /// reported while about eighty others with the same value updated normally.
    /// </summary>
    [Theory]
    // edition, Store app installed, expected in the list ("" = no blockers), must not appear
    [InlineData("Professional", false, "the Microsoft Store app is not installed for any user", "RemoveWindowsStore")]
    [InlineData("Professional", true, "", "RemoveWindowsStore")]
    [InlineData("Enterprise", true, "RemoveWindowsStore=1, honoured on this Enterprise edition", "not installed")]
    [InlineData("Education", false, "the Microsoft Store app is not installed for any user", "could not be read")]
    public void WingetScript_NamesAMissingStoreApp_AndThePolicyOnlyWhereWindowsHonoursIt(string edition, bool storeInstalled, string expected, string absent)
    {
        var store = storeInstalled ? "[pscustomobject]@{ Version = '22510.1401.12.0' }" : "@()";
        var result = WingetFunctions($$"""
            function Get-AppxPackage { [CmdletBinding()] param([switch] $AllUsers, [string] $Name)
                if ($Name -eq 'Microsoft.WindowsStore') { return {{store}} }
                [pscustomobject]@{ Version = '1.29.280.0'; Status = 'Ok'; PackageUserInformation = @([pscustomobject]@{ InstallState = 'Installed' }) }
            }
            function Get-Service { [CmdletBinding()] param([string] $Name) [pscustomobject]@{ StartType = 'Manual' } }
            function Get-PolicyValue { param([string] $Path, [string] $Name)
                switch ($Name) { 'RemoveWindowsStore' { 1 } 'EditionID' { '{{edition}}' } default { $null } }
            }
            (@(Get-StoreUpdateBlockers) -join ' | ')
            """);
        if (result is null)
        {
            return;
        }

        Assert.Equal(0, result.Value.ExitCode);
        var output = result.Value.Output.Trim();
        if (expected.Length == 0)
        {
            Assert.Equal("", output);
        }
        else
        {
            Assert.Contains(expected, output);
        }

        Assert.DoesNotContain(absent, output);
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
            + "$code = @(Invoke-WingetSelfHeal)\n\"code=$($code[-1])\"");
        if (result is null)
        {
            return;
        }

        Assert.Equal(0, result.Value.ExitCode);
        Assert.Contains(expected, result.Value.Output);
        Assert.Contains("code=1", result.Value.Output);
        Assert.DoesNotContain("nothing to repair", result.Value.Output);
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

    [Theory]
    // package version, winget version, expected text in the failure
    [InlineData("", "1.10.390", "App Installer is not installed")]
    [InlineData("1.26.5.0", "1.10.390", "App Installer 1.26.5.0 is below the minimum 1.27.0")]
    [InlineData("1.27.2.0", "1.10.390", "winget 1.10.390 is behind the latest release 1.12.390")]
    public void WingetScript_SelfHeal_WithNoKintsugiCopy_FallsBackToTheStore_AndStaysFailed(string package, string cli, string expected)
    {
        static string V(string value) => value.Length == 0 ? "$null" : $"([version]'{value}')";
        var result = WingetFunctions(
            "$MinimumAppInstallerVersion = [version]'1.27.0'\n"
            + $"function Get-AppInstallerVersion {{ {V(package)} }}\n"
            + $"function Get-WingetCliVersion {{ {V(cli)} }}\n"
            + "function Get-LatestVersion { param($AppId) '1.12.390' }\n"
            // An older server, or one whose copy is not filled yet: nothing may then be downloaded or installed.
            + "function Get-KintsugiServer { 'https://kintsugi.example.com:8443' }\n"
            + "function Invoke-RestMethod { throw 'Response status code does not indicate success: 404 (Not Found).' }\n"
            + "function Invoke-WebRequest { throw 'nothing may be downloaded' }\n"
            + "function Add-AppxProvisionedPackage { throw 'nothing may be installed' }\n"
            + "$code = @(Invoke-WingetSelfHeal)\n\"code=$($code[-1])\"");
        if (result is null)
        {
            return;
        }

        Assert.Equal(0, result.Value.ExitCode);
        Assert.Contains("code=1", result.Value.Output);
        Assert.Contains(expected, result.Value.Output);
        Assert.Contains("Kintsugi has no copy of App Installer to offer yet: Response status code does not indicate success: 404", result.Value.Output);
        Assert.Contains("Kintsugi's copy of App Installer was not installed (see above), so the Microsoft Store has to deliver the update", result.Value.Output);
        Assert.DoesNotContain("nothing may be downloaded", result.Value.Output);
        Assert.DoesNotContain("nothing may be installed", result.Value.Output);
    }

    [Theory]
    // winget after installing Kintsugi's copy, expected exit code, expected text
    [InlineData("1.12.390", "code=0", "winget is now 1.12.390")]
    [InlineData("1.11.0", "code=1", "winget 1.11.0 is behind the latest release 1.12.390 after installing Kintsugi's copy")]
    public void WingetScript_SelfHeal_InstallsKintsugisCopy_AndSucceedsOnlyWhenWingetIsThenCurrent(string after, string code, string expected)
    {
        var result = WingetFunctions($$"""
            $MinimumAppInstallerVersion = [version]'1.27.0'
            function Get-AppInstallerVersion { [version]'1.27.2.0' }
            $script:cliReads = 0
            function Get-WingetCliVersion { $script:cliReads++; if ($script:cliReads -eq 1) { [version]'1.10.390' } else { [version]'{{after}}' } }
            function Get-LatestVersion { param($AppId) '1.12.390' }
            function Install-AppInstallerFromKintsugi { param($CliVersion) [version]'{{after}}' }
            function Invoke-StoreUpdateScan { throw 'the Store is only the fallback' }
            $code = @(Invoke-WingetSelfHeal)
            "code=$($code[-1])"
            """);
        if (result is null)
        {
            return;
        }

        Assert.Equal(0, result.Value.ExitCode);
        Assert.Contains(code, result.Value.Output);
        Assert.Contains(expected, result.Value.Output);
        Assert.DoesNotContain("the Store is only the fallback", result.Value.Output);
    }

    [Fact]
    public void WingetScript_SelfHeal_SucceedsWhenAppInstallerAndWingetAreCurrent()
    {
        var result = WingetFunctions(
            "$MinimumAppInstallerVersion = [version]'1.27.0'\n"
            + "function Get-AppInstallerVersion { [version]'1.27.2.0' }\n"
            + "function Get-WingetCliVersion { [version]'1.12.390' }\n"
            + "function Get-LatestVersion { param($AppId) '1.12.390' }\n"
            + "$code = @(Invoke-WingetSelfHeal)\n\"code=$($code[-1])\"");
        if (result is null)
        {
            return;
        }

        Assert.Equal(0, result.Value.ExitCode);
        Assert.Contains("code=0", result.Value.Output);
        Assert.Contains("nothing to repair", result.Value.Output);
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

    // ---- Software titles: --install / --uninstall (IP-880) ----

    /// <summary>The five managers a software title can name, each with its install and remove commands.</summary>
    public static TheoryData<string, string, string, string> ActionManagers() => new()
    {
        { PackageManagerCatalog.Winget, WingetUpgradeScript.Build(), "& $winget install --exact --id $AppId --source (Get-PackageSource -Id $AppId) --silent", "& $winget uninstall --exact --id $AppId --silent" },
        { PackageManagerCatalog.Chocolatey, ChocolateyUpgradeScript.Build(), "choco install $AppId -y --no-progress --limit-output", "choco uninstall $AppId -y --no-progress --limit-output" },
        { PackageManagerCatalog.Homebrew, HomebrewUpgradeScript.Build(), "as_brew_owner \"$MODE\" \"$APP_ID\"", "as_brew_owner \"$MODE\" \"$APP_ID\"" },
        { PackageManagerCatalog.Flatpak, FlatpakUpgradeScript.Build(), "flatpak install --system --noninteractive --assumeyes flathub \"$APP_ID\"", "flatpak uninstall --system --noninteractive --assumeyes \"$APP_ID\"" },
        { PackageManagerCatalog.Snap, SnapUpgradeScript.Build(), "snap install \"$APP_ID\"", "snap remove \"$APP_ID\"" },
    };

    [Theory]
    [MemberData(nameof(ActionManagers))]
    public void ActionManagers_ScriptsImplementInstallAndUninstall_AsWholeTokens(string manager, string script, string install, string uninstall)
    {
        Assert.True(PackageManagerCatalog.TryGet(manager, out var entry));
        Assert.True(entry.SupportsActions);
        Assert.Equal(script, entry.BuildScript());

        Assert.True(ScriptContract.Supports(script, ScriptContract.Install));
        Assert.True(ScriptContract.Supports(script, ScriptContract.Uninstall));
        Assert.Contains(install, script);
        Assert.Contains(uninstall, script);
    }

    [Theory]
    [MemberData(nameof(ActionManagers))]
    public void ActionManagers_ScriptsAreIdempotent_TheyCheckBeforeActing(string manager, string script, string install, string uninstall)
    {
        _ = manager;
        _ = install;
        _ = uninstall;
        // Every script either asks whether the package is there first or treats the manager's own
        // "already installed" / "not installed" result as success; both print or exit 0 without acting.
        Assert.True(
            script.Contains("already in the wanted state", StringComparison.Ordinal) || script.Contains("-1978335135", StringComparison.Ordinal),
            "an action mode must exit 0 when the host is already in the wanted state");
    }

    [Fact]
    public void SnapScript_PassesClassic_OnlyForAClassicConfinementSnap()
    {
        var script = SnapUpgradeScript.Build();

        // The store reports the confinement on each channel line of `snap info`; only a classic snap
        // gets the wider grant, and a strict one is installed as before.
        Assert.Contains("snap_info=$(snap info \"$APP_ID\" 2>/dev/null || true)", script);
        Assert.Contains("grep -Eq '^[[:space:]]+[^[:space:]]+:[[:space:]].*[[:space:]]classic$' <<<\"$snap_info\"", script);
        // Never piped into grep -q: under pipefail an early exit SIGPIPEs the writer and fails the test.
        Assert.DoesNotContain("snap info \"$APP_ID\" 2>/dev/null | grep", script);
        Assert.Contains("snap install --classic \"$APP_ID\"", script);
        Assert.Contains("else\n      snap install \"$APP_ID\"\n", script);
        Assert.True(
            script.IndexOf("snap info \"$APP_ID\"", StringComparison.Ordinal) < script.IndexOf("snap install --classic", StringComparison.Ordinal),
            "the confinement must be read before installing");
    }

    [Fact]
    public void WingetScript_RepairsItsOwnRowOnUpdateOnly_AndInstallsFromTheIdsSource()
    {
        var script = WingetUpgradeScript.Build();

        // The App Installer check is winget's own --update. Without the mode guard it would also answer
        // --install/--uninstall for winget's own row, exiting 0 or 1 on the wrong question.
        Assert.Contains("if ($Mode -eq 'update' -and (Test-IsWingetItself)) {", script);
        // Get-PackageSource is defined once, before both commands that use it.
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(script, "function Get-PackageSource"));
        Assert.True(script.IndexOf("function Get-PackageSource", StringComparison.Ordinal)
            < script.IndexOf("& $winget install", StringComparison.Ordinal));
        // An uninstall names no source: a filter would turn "installed outside winget" into
        // "not installed", which counts as success, and the app would never be removed.
        Assert.Contains("& $winget uninstall --exact --id $AppId --silent", script);
        Assert.Contains("winget $Mode exited with $(Get-ExitCodeText $code)", script);
    }

    [Fact]
    public void AppStore_DoesNotSupportActions_BecauseMasInstallNeedsTheTrackId()
    {
        Assert.True(PackageManagerCatalog.TryGet(PackageManagerCatalog.AppStore, out var appStore));
        Assert.False(appStore.SupportsActions);
        Assert.False(ScriptContract.Supports(AppStoreUpgradeScript.Build(), ScriptContract.Install));
    }

    [Theory]
    [InlineData("Homebrew", "macOS")]
    [InlineData("App Store", "macOS")]
    [InlineData("winget", "Windows")]
    [InlineData("Chocolatey", "Windows")]
    [InlineData("Flatpak", "Linux")]
    [InlineData("Snap", "Linux")]
    public void Catalog_KnowsEachManagersOperatingSystem(string manager, string os)
    {
        Assert.True(PackageManagerCatalog.TryGet(manager, out var entry));
        Assert.Equal(os, entry.OperatingSystem);
    }

    [Fact]
    public void PowerShellArgumentParsing_AcceptsBothActionModes_AsWholeArguments()
    {
        var parsing = PowerShellUpgradeScript.ArgumentParsing;

        Assert.Contains("'--install' { if ($Mode) { Show-Usage }; $Mode = 'install' }", parsing);
        Assert.Contains("'--uninstall' { if ($Mode) { Show-Usage }; $Mode = 'uninstall' }", parsing);
    }

    [Fact]
    public void HomebrewScript_RunsActionsAsThePrefixOwner_NeverAsRoot()
    {
        var script = HomebrewUpgradeScript.Build();

        // The daemon runs every action row as root, and Homebrew refuses root.
        Assert.Contains("brew_owner=$(stat -f %Su \"$brew_home\")", script);
        Assert.Contains("sudo -u \"$brew_owner\" -H", script);
        Assert.Contains("[ \"$brew_owner\" = \"root\" ]", script);
        // The --update path is unchanged: it still upgrades by name, as the logged-in user.
        Assert.Contains("brew upgrade \"$APP_NAME\"", script);
    }

    [Theory]
    [InlineData("--install", true)]
    [InlineData("x --install y", true)]
    [InlineData("'--install'", true)]
    [InlineData("--install)", true)]
    [InlineData("--installed", false)]
    [InlineData("--install-dir", false)]
    [InlineData("--install_x", false)]
    [InlineData("--uninstall", false)]
    [InlineData("", false)]
    public void ScriptContract_Supports_MatchesTheModeAsAWholeToken(string script, bool expected)
    {
        Assert.Equal(expected, ScriptContract.Supports(script, ScriptContract.Install));
    }

    [Fact]
    public void ScriptContract_Supports_DoesNotFindInstallInsideUninstall()
    {
        Assert.False(ScriptContract.Supports("--uninstall", ScriptContract.Install));
        Assert.True(ScriptContract.Supports("--uninstall", ScriptContract.Uninstall));
        Assert.False(ScriptContract.Supports(null, ScriptContract.Uninstall));
    }

    [Fact]
    public void ScriptContract_ModeFor_MapsEachAction()
    {
        Assert.Equal("--install", ScriptContract.ModeFor("Install"));
        Assert.Equal("--uninstall", ScriptContract.ModeFor("Uninstall"));
        Assert.Throws<ArgumentOutOfRangeException>(() => ScriptContract.ModeFor("Upgrade"));
    }
}
