namespace Kintsugi.Application.UpgradePaths;

/// <summary>
/// The Windows counterpart to <see cref="HomebrewUpgradeScript"/>: the fixed
/// --appName/--appId/--update-version/--update script for everything winget manages, winget itself
/// included. Never AI-generated — winget's upgrade mechanics are already fully known.
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
        // it would need to upgrade itself, which is the deadlock that left 56 hosts behind.
        const string updateCommand = """
            function Get-PackageSource {
                param([string] $Id)
                if ($Id -cmatch '^(?=.*\d)[A-Z0-9]{12,14}$') { return 'msstore' }
                return 'winget'
            }

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

        // winget's own row: repair App Installer instead of asking winget to upgrade itself.
        //
        // Why not `winget upgrade winget`: a winget older than the Store's certificate rotation cannot
        // validate the msstore certificate, so it fails with 0x8A15005E before it can fetch the newer
        // winget that would fix it. The way out has to come from outside winget, and it has to work as
        // SYSTEM, which on the affected hosts has no PowerShell Gallery registered — so no
        // Install-Module and no Repair-WinGetPackageManager, only what ships with Windows.
        //
        // The steps, each one a hard stop when it fails rather than a warning: read the version that
        // is really installed; if it is below the minimum, take the latest release from the winget-cli
        // GitHub API (file names are read from the API's answer, never written down here); download the
        // bundle, the dependencies and the licence; check the Authenticode signature of the bundle and
        // of every dependency package (Status Valid, signed by Microsoft Corporation) before anything
        // is installed; provision the dependencies for this host's architecture, then the bundle with
        // its licence, machine-wide; read the version again and succeed only if it is now at or above
        // the minimum. The temporary folder is always removed.
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

            # The newest release tag from the releases/latest redirect: no API call, so no rate limit.
            function Get-LatestReleaseTag {
                $location = Get-RedirectLocation -Uri 'https://github.com/microsoft/winget-cli/releases/latest'
                $tag = ($location -split '/')[-1]
                if (-not $tag) { throw 'could not read the latest winget-cli release tag' }
                return $tag
            }

            # The files a release carries, as { name, browser_download_url }. Read from the release page's own asset
            # list on github.com, which is not the API and has no 60-requests-an-hour limit per address (an office
            # with more than 60 hosts below the minimum shared one and the rest failed). The API is only the fallback
            # when that page yields nothing, so a change to the page degrades to the old behaviour instead of breaking.
            function Get-ReleaseAssets {
                param([string] $Tag)
                $assets = @()
                try {
                    $page = Invoke-WebRequest -UseBasicParsing -ErrorAction Stop -Headers @{ 'User-Agent' = 'kintsugi-agent' } `
                        -Uri "https://github.com/microsoft/winget-cli/releases/expanded_assets/$Tag"
                    foreach ($match in [regex]::Matches([string] $page.Content, 'href="(/microsoft/winget-cli/releases/download/[^"?#]+)"')) {
                        $path = $match.Groups[1].Value
                        $assets += [pscustomobject]@{ name = [IO.Path]::GetFileName($path); browser_download_url = 'https://github.com' + $path }
                    }
                } catch {
                    $assets = @()
                }
                if ($assets.Count -gt 0) { return $assets }

                $release = Invoke-RestMethod -Method Get -ErrorAction Stop `
                    -Headers @{ 'User-Agent' = 'kintsugi-agent'; 'Accept' = 'application/vnd.github+json' } `
                    -Uri "https://api.github.com/repos/microsoft/winget-cli/releases/tags/$Tag"
                if ($release.PSObject.Properties['assets']) { return @($release.assets) }
                return @()
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

            function Assert-MicrosoftSigned {
                param([string] $Path)
                $signature = Get-AuthenticodeSignature -FilePath $Path
                $name = [IO.Path]::GetFileName($Path)
                if ($signature.Status -ne 'Valid') { throw "$name has no valid signature (status $($signature.Status))" }
                $subject = if ($signature.SignerCertificate) { $signature.SignerCertificate.Subject } else { '' }
                if ($subject -notmatch '(^|, )O=Microsoft Corporation(,|$)') { throw "$name is signed, but not by Microsoft Corporation ($subject)" }
            }

            function Save-ReleaseAsset {
                param($Asset, [string] $Folder)
                # The file must come from the winget-cli project's own release downloads: the API answer
                # names the URL, and a URL anywhere else is not what this repair is allowed to fetch.
                if ($Asset.browser_download_url -notmatch '^https://github\.com/microsoft/winget-cli/releases/download/') {
                    throw "unexpected download address for $($Asset.name): $($Asset.browser_download_url)"
                }
                $target = Join-Path $Folder ([IO.Path]::GetFileName([string] $Asset.name))
                Invoke-WebRequest -UseBasicParsing -ErrorAction Stop -Uri $Asset.browser_download_url -OutFile $target
                return $target
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
                [Console]::Out.WriteLine("$reason; repairing from the latest winget-cli release")

                $ProgressPreference = 'SilentlyContinue'
                $work = Join-Path ([IO.Path]::GetTempPath()) ('kintsugi-winget-' + [guid]::NewGuid().ToString('N'))
                New-Item -ItemType Directory -Path $work -Force | Out-Null
                try {
                    try {
                        $tag = Get-LatestReleaseTag
                        $assets = @(Get-ReleaseAssets -Tag $tag)
                    } catch {
                        if (Test-NetworkOrTlsError $_) { Write-Blocked 'reading the latest winget-cli release' $_ }
                        else { [Console]::Error.WriteLine("could not read the latest winget-cli release: $_") }
                        return 1
                    }

                    # By pattern, from what the release really carries.
                    $bundleAsset = $assets | Where-Object { $_.name -match '\.msixbundle$' } | Select-Object -First 1
                    $dependenciesAsset = $assets | Where-Object { $_.name -match '_Dependencies\.zip$' } | Select-Object -First 1
                    $licenseAsset = $assets | Where-Object { $_.name -match '_License1\.xml$' } | Select-Object -First 1
                    if (-not $bundleAsset -or -not $dependenciesAsset -or -not $licenseAsset) {
                        [Console]::Error.WriteLine('the latest winget-cli release does not carry a bundle, a dependencies archive and a licence; nothing was installed')
                        return 1
                    }

                    try {
                        $bundle = Save-ReleaseAsset $bundleAsset $work
                        $dependenciesZip = Save-ReleaseAsset $dependenciesAsset $work
                        $license = Save-ReleaseAsset $licenseAsset $work
                    } catch {
                        if (Test-NetworkOrTlsError $_) { Write-Blocked 'downloading the winget-cli release' $_ }
                        else { [Console]::Error.WriteLine("could not download the winget-cli release: $_") }
                        return 1
                    }

                    # Nothing is installed before every package has been checked.
                    try {
                        # A 32-bit PowerShell on 64-bit Windows reports x86 here and the real one in ARCHITEW6432.
                        $architecture = if ($env:PROCESSOR_ARCHITEW6432) { $env:PROCESSOR_ARCHITEW6432 } else { $env:PROCESSOR_ARCHITECTURE }
                        $archFolder = switch ($architecture) { 'ARM64' { 'arm64' } 'x86' { 'x86' } default { 'x64' } }
                        $extract = Join-Path $work 'dependencies'
                        Expand-Archive -LiteralPath $dependenciesZip -DestinationPath $extract -Force
                        $dependencies = @(Get-ChildItem -LiteralPath (Join-Path $extract $archFolder) -Filter '*.appx' -File -ErrorAction Stop)
                        if ($dependencies.Count -eq 0) { throw "the dependencies archive has no packages for $archFolder" }
                        Assert-MicrosoftSigned $bundle
                        foreach ($dependency in $dependencies) { Assert-MicrosoftSigned $dependency.FullName }
                        # The licence is data, not a signed package: it only has to be a well-formed document.
                        $licenseDocument = New-Object System.Xml.XmlDocument
                        $licenseDocument.Load($license)
                    } catch {
                        [Console]::Error.WriteLine("refusing to install, a downloaded file did not check out: $_")
                        return 1
                    }

                    try {
                        # Dependencies first, for this architecture. One already present is not a reason to
                        # stop: the bundle below is what decides whether the repair worked.
                        foreach ($dependency in $dependencies) {
                            try {
                                Add-AppxProvisionedPackage -Online -PackagePath $dependency.FullName -SkipLicense -ErrorAction Stop | Out-Null
                            } catch {
                                [Console]::Error.WriteLine("dependency $($dependency.Name) was not provisioned (it may already be present): $($_.Exception.Message)")
                            }
                        }
                        Add-AppxProvisionedPackage -Online -PackagePath $bundle -LicensePath $license -ErrorAction Stop | Out-Null
                    } catch {
                        [Console]::Error.WriteLine("installing App Installer failed: $_")
                        return 1
                    }

                    $after = Get-AppInstallerVersion
                    # When the reason was that winget is behind the latest release, the package must also have moved:
                    # reporting success for a repair that changed nothing is how a row stayed Update Available forever.
                    $mustMove = $reason -like 'winget * is behind the latest release *'
                    if ($after -and $after -ge $MinimumAppInstallerVersion -and (-not $mustMove -or ($before -and $after -gt $before))) {
                        [Console]::Out.WriteLine("App Installer is now $after")
                        return 0
                    }
                    $afterText = if ($after) { "$after" } else { 'not found' }
                    [Console]::Error.WriteLine("App Installer is $afterText after the repair, and the minimum is $MinimumAppInstallerVersion")
                    return 1
                } finally {
                    Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
                }
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

            # --update mode: runs on the managed Windows host itself, where winget actually exists.
            #
            # winget's own row is repaired, not upgraded with winget (see Invoke-WingetSelfHeal above), and it runs
            # before winget is even looked for: the repair has to work on a host whose winget is too old
            # to be useful, or missing.
            if (Test-IsWingetItself) {
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
