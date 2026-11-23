namespace Kintsugi.Application.UpgradePaths;

/// <summary>
/// The Windows counterpart to <see cref="HomebrewUpgradeScript"/>: the fixed
/// --appName/--appId/--update-version/--update/--install/--uninstall script for everything winget
/// manages, winget itself included. Never AI-generated — winget's upgrade mechanics are already fully known.
/// </summary>
/// <remarks>
/// <para>
/// Same two-audience split as the Homebrew script, just in PowerShell: <c>--update-version</c> runs
/// on the (Linux) API server under <c>pwsh</c> and may only make HTTP calls, while <c>--update</c>
/// runs on the managed Windows host where <c>winget.exe</c> actually exists. The package id — not
/// the display name — is what winget addresses a package by, so <c>--appId</c> is the load-bearing
/// argument here (the Windows agent reports a winget package's id as its
/// <c>applicationIdentifier</c>); neither it nor the name is ever baked into the text, so
/// <see cref="Build"/> returns byte-identical content for every winget package and one human
/// "Sign Script" review covers them all.
/// </para>
/// <para>
/// One text for winget's own row and every package it manages (see
/// <see cref="RecognizedPackageManager.BuildScript"/>): winget's own row is told
/// apart at runtime by <c>--appName</c> — the name the Windows agent reports it under
/// (<c>system_info::WINGET_NAME</c>) — because the Applications screen shows a manager's script once,
/// on the manager's row, on behalf of every package nested under it, and that is only honest if the
/// manager's bytes are its children's bytes. See the remarks on <see cref="HomebrewUpgradeScript"/>,
/// where the reasoning lives in full. winget is not a winget package under its own name (it ships
/// inside App Installer, <c>Microsoft.AppInstaller</c>), so that row needs a different version source
/// and a different id to upgrade — hence the branch rather than Chocolatey's and Snap's plain reuse.
/// </para>
/// </remarks>
public static class WingetUpgradeScript
{
    public static string Build()
    {
        // The winget-pkgs repository's manifest tree is the canonical list of published versions,
        // and a directory listing of it needs no auth. It *is* rate-limited to 60 requests/hour for
        // an unauthenticated caller, which a large fleet scan can reach — but this is the secondary
        // source for a winget row's version, not the primary one: the agent already reports each
        // package's available version straight from `winget` itself on every check-in, which
        // RegisterApplicationsCommandHandler seeds the row from. A throttled check here just leaves
        // that seeded value in place.
        //
        // Microsoft's own storeedgefd REST source was tried first and dropped: it answers
        // /packageManifests/<id> with a response whose Versions array is empty, so it looked like a
        // successful lookup while actually yielding nothing usable.
        const string latestVersionLogic = """
            # winget's own row. The agent reports winget itself under this name (system_info::WINGET_NAME),
            # and it is not a package in winget-pkgs under it: it ships inside App Installer. One script
            # serves it and everything it manages so that one review covers all of them; this is where
            # the two part ways.
            function Test-IsWingetItself {
                return ($AppName -ieq 'winget')
            }

            function Get-LatestVersion {
                param([string] $AppId)
                if (Test-IsWingetItself) {
                    # App Installer's releases are published on GitHub. The releases/latest redirect
                    # names the newest tag with no API call, and so no rate limit, at all.
                    $location = Get-RedirectLocation -Uri 'https://github.com/microsoft/winget-cli/releases/latest'
                    $tag = ($location -split '/')[-1]
                    if (-not $tag) { throw "could not read a release tag while checking $AppId" }
                    return $tag.TrimStart('v')
                }

                # winget-pkgs' fixed layout: manifests/<lowercased first letter of the id>/<the id
                # split on '.'>/, where each subdirectory name is a published version.
                $segments = $AppId -split '\.'
                $path = 'manifests/' + $AppId.Substring(0, 1).ToLowerInvariant() + '/' + ($segments -join '/')
                $listing = Invoke-RestMethod -Method Get -ErrorAction Stop `
                    -Headers @{ 'User-Agent' = 'kintsugi-agent'; 'Accept' = 'application/vnd.github+json' } `
                    -Uri "https://api.github.com/repos/microsoft/winget-pkgs/contents/$path"

                # Directories only, and only ones that look like a version — the tree also carries
                # non-version entries (a '.validation' directory, the odd stray file).
                $versions = @($listing | Where-Object { $_.type -eq 'dir' -and $_.name -match '^\d' } | ForEach-Object { $_.name })
                if ($versions.Count -eq 0) { throw "no published versions found for $AppId" }
                return ($versions | Sort-Object { ConvertTo-SortableVersion $_ } | Select-Object -Last 1)
            }
            """;

        // The agent runs --update as SYSTEM, and SYSTEM has no `winget` on PATH: the one on PATH is a
        // per-user App Execution Alias (%LOCALAPPDATA%\Microsoft\WindowsApps\winget.exe). So, like
        // the Windows agent's system_info::find_packaged_winget, run the real binary inside the
        // newest App Installer package under Program Files\WindowsApps — a directory only
        // TrustedInstaller can write to, which is also why it is preferred over anything on PATH.
        // Without this, the agent seeing winget (windows-agent 0.12.2) would turn every
        // winget-managed application into a failed patch.
        //
        // Two refinements on the agent's version: a folder is only a candidate if winget.exe is
        // actually in it, so a half-removed newest package falls back to the next one rather than
        // to PATH, and the host's own architecture wins a tie between x64 and arm64 packages.
        // The bare name remains the last resort, for an interactive or otherwise PATH-equipped
        // caller.
        const string wingetResolution = """
            function Resolve-Winget {
                $nativeArch = if ($env:PROCESSOR_ARCHITECTURE -ieq 'ARM64') { 'arm64' } else { 'x64' }
                $windowsApps = Join-Path $env:ProgramFiles 'WindowsApps'
                $packaged = @(Get-ChildItem -LiteralPath $windowsApps -Directory -ErrorAction SilentlyContinue | ForEach-Object {
                    if ($_.Name -match '^Microsoft\.DesktopAppInstaller_(\d+(?:\.\d+)*)_(x64|arm64)__8wekyb3d8bbwe$') {
                        $exe = Join-Path $_.FullName 'winget.exe'
                        if (Test-Path -LiteralPath $exe -PathType Leaf) {
                            [pscustomobject]@{ Path = $exe; Native = ($Matches[2] -eq $nativeArch); Version = (ConvertTo-SortableVersion $Matches[1]) }
                        }
                    }
                })
                if ($packaged.Count -gt 0) {
                    return ($packaged | Sort-Object -Property @{ Expression = 'Native'; Descending = $true }, @{ Expression = 'Version'; Descending = $true } | Select-Object -First 1).Path
                }
                $onPath = Get-Command winget -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
                if ($onPath) { return $onPath.Path }
                return $null
            }
            """;

        // --exact so a partial id match can never upgrade a different package, and every
        // interactivity/agreement flag winget needs to run unattended — without them it blocks
        // forever waiting on a prompt no one will ever see.
        //
        // The source follows the kind of id. A winget-pkgs id is always Publisher.Package, so it goes to
        // --source winget, and never to msstore: without --source winget, winget also queries msstore and an
        // msstore failure then breaks upgrades that have nothing to do with the Store — the 0x8A15005E
        // (msstore certificate pinning) failure on a host whose winget is too old to validate the current Store
        // certificate, which took LibreOffice and everything else down with it. A Microsoft Store product id
        // (9NKSQGP7F2NH, XP8BT8DW290MPQ: 12 to 14 upper-case letters and digits, with no dot) is not in the
        // winget source at all, and the Windows agent reports Store apps under winget with that id, so asking the
        // winget source for it answered "No package found matching input criteria" and every Store app with an
        // update became a failed patch every cycle. It goes to --source msstore, where a failure belongs to the
        // Store app alone. The seeded "Managed elsewhere" list still covers the Store apps an organisation
        // decides not to patch this way.
        //
        // winget's own row never reaches this command: it is not upgraded with `winget upgrade` at
        // all (see selfHeal below). It cannot be — an old winget cannot validate the certificate
        // it would need to upgrade itself, a deadlock that leaves a host behind for good.
        const string packageSource = """
            function Get-PackageSource {
                param([string] $Id)
                if ($Id -cmatch '^(?=.*\d)[A-Z0-9]{12,14}$') { return 'msstore' }
                return 'winget'
            }
            """;

        const string updateCommand = """
            $source = Get-PackageSource -Id $AppId
            & $winget upgrade --exact --id $AppId --source $source --silent --accept-package-agreements --accept-source-agreements --disable-interactivity
            """;

        // Exit codes as people can search for them. winget exits with an HRESULT and PowerShell hands
        // it back as a signed 32-bit integer, so a failure used to read -1978335138 in the log while
        // every error table calls it 0x8A15005E. The table is generated from ExitCodeHints.Known, so
        // the hint in this script's output and the one the server shows beside a stored failure are
        // one text.
        var knownCodes = string.Join(
            "\n",
            ExitCodeHints.Known.Select(k => $"    '{ExitCodeHints.ToHex(k.Code)}' = '{k.Hint.Replace("'", "''", StringComparison.Ordinal)}'"));

        const string exitCodeText = """
            function Get-ExitCodeText {
                param([int] $Code)
                $hex = '0x{0:X8}' -f $Code
                if ($KnownExitCodes.ContainsKey($hex)) { return "$hex ($($KnownExitCodes[$hex]))" }
                return $hex
            }
            """;

        // winget's own row: check App Installer instead of asking winget to upgrade itself, and report a host
        // that needs it. Nothing is downloaded or installed.
        //
        // Why not `winget upgrade winget`: a winget older than the Store's certificate rotation cannot
        // validate the msstore certificate, so it fails with 0x8A15005E before it can fetch the newer
        // winget that would fix it. So this row never asks winget to upgrade itself.
        //
        // The steps: read the App Installer version that is really installed and the version winget reports,
        // and read the latest release tag from github.com (a tag only, no file). If App Installer is missing or
        // below the minimum, or winget is behind the latest release, the script asks Windows for a Microsoft
        // Store update scan (the MDM bridge's UpdateScanMethod, which works as SYSTEM) so the Store delivers
        // the signed package, lists what on the host may be stopping the Store (policy values, InstallService,
        // the package's own state), and exits 1: a slow Store must not read as a patched machine. The scan
        // covers every Store app on the host, not App Installer alone. If it is current, it exits 0. If the
        // latest release cannot be read it exits 1, since "nothing to repair" without that proof would leave
        // the row Update Available.
        //
        // This script used to download the App Installer bundle from the winget-cli GitHub releases and
        // provision it on every host. That was removed: production hosts should not pull binaries straight from
        // GitHub. TODO: self-hosted App Installer distribution will be added in a future PR.
        //
        // The Store-scan part was first edited into the signed script on a server and approved as
        // kintsugi-approvals PR 32; this text is kept byte-identical to it so a regenerated script still
        // matches what was reviewed.
        //
        // A TLS or certificate failure while reaching GitHub is what SSL inspection looks like from
        // here. It exits with a message that starts "Blocked: network / SSL inspection" so it can be
        // told apart from a script that is simply broken.
        const string selfHeal = """
            # What SSL inspection and a blocked network look like from here. A plain timeout is deliberately not in
            # this list: a slow or briefly unreachable GitHub is not a policy to be fixed, and calling it "Blocked:
            # network / SSL inspection" sends someone to change a firewall that is not the problem.
            function Test-NetworkOrTlsError {
                param($ErrorRecord)
                $text = ($ErrorRecord | Out-String) + ' ' + ($ErrorRecord.Exception | Out-String)
                return ($text -match '(?i)SSL|TLS|certificate|trust relationship|secure channel|remote name could not be resolved|name or service not known|unable to connect|connection (was )?(closed|refused|reset)|proxy authentication')
            }

            function Write-Blocked {
                param([string] $Step, $ErrorRecord)
                [Console]::Error.WriteLine("Blocked: network / SSL inspection while $Step. Allow the Microsoft Store and GitHub endpoints through TLS inspection. $($ErrorRecord.Exception.Message)")
            }

            # The version `winget --version` reports ("v1.12.390" gives 1.12.390), or null when winget cannot be run.
            # This is the CLI's own numbering, which is what the winget row's version and the latest release tag use;
            # it is not the App Installer *package* version below, which is numbered 1.27.x.0.
            function Get-WingetCliVersion {
                $exe = Resolve-Winget
                if (-not $exe) { return $null }
                try {
                    $text = (& $exe --version 2>$null | Out-String).Trim()
                } catch {
                    return $null
                }
                $parsed = $null
                if ($text -match 'v?(\d+(?:\.\d+){1,3})' -and [version]::TryParse($Matches[1], [ref] $parsed)) { return $parsed }
                return $null
            }

            # Why App Installer needs repairing, or nothing when it does not. Two reasons, from two numberings that
            # must not be mixed: the package version is below the minimum (the certificate deadlock), or winget's own
            # version is behind the latest release. Deciding on the package version alone left a host with App
            # Installer 1.27 or newer and an old winget "with nothing to repair", exit 0 and no change, so its row
            # stayed Update Available and ran again every cycle. A version that cannot be read never causes a repair.
            function Get-RepairReason {
                param($PackageVersion, $CliVersion, $LatestVersion, [version] $Minimum)
                if (-not $PackageVersion) { return 'App Installer is not installed' }
                if ($PackageVersion -lt $Minimum) { return "App Installer $PackageVersion is below the minimum $Minimum" }
                $latest = $null
                if ($LatestVersion -and [version]::TryParse(([string] $LatestVersion).TrimStart('v'), [ref] $latest) -and $CliVersion -and $CliVersion -lt $latest) {
                    return "winget $CliVersion is behind the latest release $latest"
                }
                return $null
            }

            # The highest App Installer version this host has, from every place one can show up: the
            # packages registered for any user, the ones provisioned for new users, and the staged
            # folders under Program Files\WindowsApps (the same place Resolve-Winget runs winget from).
            # Get-AppxPackage alone misses a package that is provisioned but not yet registered for a
            # user, which is exactly the state this repair leaves behind. These are App Installer
            # *package* versions (1.25.390.0), not the `winget --version` text (1.10.390).
            function Get-AppInstallerVersion {
                $found = @()
                $parsed = $null
                foreach ($package in @(Get-AppxPackage -AllUsers -Name Microsoft.DesktopAppInstaller -ErrorAction SilentlyContinue)) {
                    if ([version]::TryParse([string] $package.Version, [ref] $parsed)) { $found += $parsed }
                }
                foreach ($package in @(Get-AppxProvisionedPackage -Online -ErrorAction SilentlyContinue | Where-Object { $_.DisplayName -eq 'Microsoft.DesktopAppInstaller' })) {
                    if ([version]::TryParse([string] $package.Version, [ref] $parsed)) { $found += $parsed }
                }
                $windowsApps = Join-Path $env:ProgramFiles 'WindowsApps'
                foreach ($folder in @(Get-ChildItem -LiteralPath $windowsApps -Directory -ErrorAction SilentlyContinue)) {
                    if ($folder.Name -match '^Microsoft\.DesktopAppInstaller_(\d+(?:\.\d+){1,3})_') {
                        if ([version]::TryParse($Matches[1], [ref] $parsed)) { $found += $parsed }
                    }
                }
                if ($found.Count -eq 0) { return $null }
                return ($found | Sort-Object -Descending | Select-Object -First 1)
            }

            # One registry value, or null when the key or the value is absent (the normal case: most hosts set no policy).
            function Get-PolicyValue {
                param([string] $Path, [string] $Name)
                try {
                    $property = (Get-ItemProperty -LiteralPath $Path -ErrorAction Stop).PSObject.Properties[$Name]
                    if ($property) { return $property.Value }
                } catch { }
                return $null
            }

            # Why the Microsoft Store may not be updating App Installer here: the policy values a Store lockdown sets
            # (Group Policy or Intune), the Store install service, and the state of the App Installer package itself.
            # Each finding names the setting so the failure says what to change. Read-only: nothing here changes anything.
            function Get-StoreUpdateBlockers {
                $blockers = @()
                $store = 'HKLM:\SOFTWARE\Policies\Microsoft\WindowsStore'
                if ((Get-PolicyValue $store 'RemoveWindowsStore') -eq 1) { $blockers += 'the Microsoft Store is removed by policy (RemoveWindowsStore=1)' }
                if ((Get-PolicyValue $store 'DisableStoreApps') -eq 1) { $blockers += 'Store apps are disabled by policy (DisableStoreApps=1)' }
                if ((Get-PolicyValue $store 'AutoDownload') -eq 2) { $blockers += 'automatic Store app updates are turned off by policy (AutoDownload=2)' }
                if ((Get-PolicyValue $store 'RequirePrivateStoreOnly') -eq 1) { $blockers += 'the Store is limited to the private store by policy (RequirePrivateStoreOnly=1)' }
                if ((Get-PolicyValue 'HKLM:\SOFTWARE\Microsoft\PolicyManager\current\device\ApplicationManagement' 'AllowAppStoreAutoUpdate') -eq 0) { $blockers += 'Intune does not allow Store auto-update (ApplicationManagement/AllowAppStoreAutoUpdate=0)' }
                try {
                    # A stopped service is normal (it starts on demand); only a disabled or missing one is a blocker.
                    $service = Get-Service -Name InstallService -ErrorAction Stop
                    if ("$($service.StartType)" -eq 'Disabled') { $blockers += 'the Microsoft Store Install Service (InstallService) is disabled' }
                } catch { $blockers += 'the Microsoft Store Install Service (InstallService) was not found or could not be read' }
                try {
                    $packages = @(Get-AppxPackage -AllUsers -Name Microsoft.DesktopAppInstaller -ErrorAction Stop)
                    if ($packages.Count -eq 0) { $blockers += 'App Installer is not registered for any user' }
                    foreach ($package in $packages) {
                        if ("$($package.Status)" -ne 'Ok') { $blockers += "App Installer $($package.Version) has package status $($package.Status)" }
                    }
                    $newest = $packages | Sort-Object { [version] $_.Version } -Descending | Select-Object -First 1
                    if ($newest -and @($newest.PackageUserInformation | Where-Object { "$($_.InstallState)" -eq 'Installed' }).Count -eq 0) {
                        $blockers += "App Installer $($newest.Version) is only staged, not installed for any user"
                    }
                } catch { $blockers += "the App Installer package state could not be read: $($_.Exception.Message)" }
                return $blockers
            }

            # Asks Windows to run a Store update scan (the MDM bridge's UpdateScanMethod, which works as SYSTEM) and
            # returns the exact result as text. The scan only starts the work: the Store installs in the background.
            function Invoke-StoreUpdateScan {
                try {
                    $manager = Get-CimInstance -Namespace 'root\cimv2\mdm\dmmap' -ClassName 'MDM_EnterpriseModernAppManagement_AppManagement01' -ErrorAction Stop | Select-Object -First 1
                    $result = Invoke-CimMethod -InputObject $manager -MethodName UpdateScanMethod -ErrorAction Stop
                    $property = $result.PSObject.Properties['ReturnValue']
                    $code = if ($property) { $property.Value } else { 'not returned' }
                    return "Store update scan: UpdateScanMethod ReturnValue=$code"
                } catch {
                    return "Store update scan could not be started: $($_.Exception.Message)"
                }
            }

            function Invoke-WingetSelfHeal {
                # Before the first web call, which reads the latest release: Windows PowerShell 5.1 can leave TLS 1.2 out
                # of its defaults, and github.com refuses anything older, so that read would fail on every run.
                [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
                $before = Get-AppInstallerVersion
                $latest = $null
                $latestError = $null
                try { $latest = Get-LatestVersion -AppId 'winget' } catch { $latestError = $_ }
                $cli = Get-WingetCliVersion
                $reason = Get-RepairReason -PackageVersion $before -CliVersion $cli -LatestVersion $latest -Minimum $MinimumAppInstallerVersion
                if (-not $reason) {
                    # The server read a newer release to show this row as Update Available. Without reading it here too,
                    # "nothing to repair" would exit 0, change nothing and run again every cycle, so it is a failure.
                    if (-not $latest) {
                        if ($latestError -and (Test-NetworkOrTlsError $latestError)) { Write-Blocked 'reading the latest winget release' $latestError }
                        else { [Console]::Error.WriteLine("could not read the latest winget release, so cannot tell whether winget is behind it: $latestError") }
                        return 1
                    }
                    $beforeNote = if ($before) { "$before" } else { 'unknown' }
                    $cliNote = if ($cli) { "winget $cli" } else { 'winget version unknown' }
                    [Console]::Out.WriteLine("App Installer $beforeNote is at or above the minimum $MinimumAppInstallerVersion and $cliNote is not behind the latest release $latest; nothing to repair")
                    return 0
                }
                # Kintsugi does not download or install App Installer. It used to fetch the bundle from GitHub and
                # provision it fleet-wide; that is removed. What it does now is ask Windows to run a Microsoft Store
                # update scan, so the Store (not Kintsugi) delivers the signed package, and say why if it cannot.
                # This host is still behind, so the row stays failed (exit 1) until a later run finds it current:
                # a slow Store must not read as a patched machine.
                #
                # TODO: self-hosted App Installer distribution (a copy Kintsugi serves and verifies itself) will be
                # added in a future PR, for hosts whose Store is blocked for good.
                $scan = Invoke-StoreUpdateScan
                $blockers = @(Get-StoreUpdateBlockers)
                $blockerText = if ($blockers.Count -gt 0) { 'Possible Store blockers on this host: ' + ($blockers -join '; ') + '.' } else { 'No Store blocker was found on this host.' }
                [Console]::Error.WriteLine("$reason. $scan. $blockerText Kintsugi does not download or install App Installer, so nothing was installed by Kintsugi. The Microsoft Store has to deliver the update; this host stays failed until it is on the latest version.")
                return 1
            }
            """;

        // --install/--uninstall, for software titles: idempotent, as SYSTEM, no GUI. winget's own
        // "already installed", "no upgrade applies" and "no installed package found" codes are the
        // already-in-the-wanted-state cases and count as success, as does "reboot required to finish".
        // winget itself ships inside App Installer and is not something winget can add or remove.
        //
        // An install names its source the way an upgrade does (Get-PackageSource): a Store product id from
        // msstore, everything else from winget, so an msstore failure cannot break an unrelated install. An
        // uninstall names none: it acts on what is installed, and a source filter on an app installed outside
        // winget would answer "no installed package found" -- which counts as success here, so the app would
        // silently never be removed.
        const string actionModes = """
            # --install / --uninstall: idempotent, so the already-in-that-state codes are success.
            # 0x8A15002B no applicable upgrade, 0x8A150061 already installed, 0x8A150014 no installed
            # package found, 0x8A150109 a reboot is required to finish.
            if ($Mode -eq 'install' -or $Mode -eq 'uninstall') {
                if (Test-IsWingetItself) {
                    [Console]::Error.WriteLine('winget ships with App Installer and cannot install or remove itself')
                    exit 1
                }
                if ($Mode -eq 'install') {
                    & $winget install --exact --id $AppId --source (Get-PackageSource -Id $AppId) --silent --accept-package-agreements --accept-source-agreements --disable-interactivity
                    $okCodes = @(0, -1978335189, -1978335135, -1978334967)
                } else {
                    & $winget uninstall --exact --id $AppId --silent --accept-source-agreements --disable-interactivity
                    $okCodes = @(0, -1978335212, -1978334967)
                }
                $code = $LASTEXITCODE
                if ($okCodes -contains $code) {
                    exit 0
                }
                [Console]::Error.WriteLine("winget $Mode exited with $(Get-ExitCodeText $code)")
                exit 1
            }
            """;

        return $$"""
            #Requires -Version 5.1
            Set-StrictMode -Version Latest
            $ErrorActionPreference = 'Stop'

            {{PowerShellUpgradeScript.VersionSortHelper}}

            {{PowerShellUpgradeScript.RedirectLocationHelper}}

            {{PowerShellUpgradeScript.ArgumentParsing}}

            # The App Installer *package* version below which winget's own row repairs App Installer
            # (as Get-AppxPackage reports it, e.g. 1.25.390.0 — not the `winget --version` text). The
            # default, 1.27.0, is a starting point: edit it here and re-sign the script to move it.
            $MinimumAppInstallerVersion = [version] '1.27.0'

            $KnownExitCodes = @{
            {{knownCodes}}
            }

            {{exitCodeText}}

            {{latestVersionLogic}}

            {{wingetResolution}}

            {{packageSource}}

            {{selfHeal}}

            if ($Mode -eq 'update-version') {
                try {
                    $version = Get-LatestVersion -AppId $AppId
                } catch {
                    [Console]::Error.WriteLine("could not determine the latest version: $_")
                    exit 1
                }
                if (-not $version) {
                    [Console]::Error.WriteLine('could not determine the latest version')
                    exit 1
                }
                [Console]::Out.WriteLine($version)
                exit 0
            }

            # Every other mode runs on the managed Windows host itself, where winget actually exists.
            #
            # winget's own row is repaired, not upgraded with winget (see Invoke-WingetSelfHeal above), and it runs
            # before winget is even looked for: the repair has to work on a host whose winget is too old
            # to be useful, or missing. --update only: --install/--uninstall refuse winget's own row below.
            if ($Mode -eq 'update' -and (Test-IsWingetItself)) {
                # An answer that is missing is a failure, never a success: [int] $null would be 0.
                $repair = @(Invoke-WingetSelfHeal)
                $repairCode = if ($repair.Count -gt 0) { [int] $repair[-1] } else { 1 }
                exit $repairCode
            }

            $winget = Resolve-Winget
            if (-not $winget) {
                [Console]::Error.WriteLine('winget is not installed (it ships with App Installer, from the Microsoft Store)')
                exit 1
            }

            {{actionModes}}

            {{updateCommand}}
            $code = $LASTEXITCODE
            # 0x8A15002B ("no applicable upgrade found") is winget's way of saying the package is
            # already current. That is the idempotent-success case this contract requires, not a
            # failure.
            if ($code -eq 0 -or $code -eq -1978335189) {
                exit 0
            }
            [Console]::Error.WriteLine("winget upgrade exited with $(Get-ExitCodeText $code)")
            exit 1
            """;
    }
}
