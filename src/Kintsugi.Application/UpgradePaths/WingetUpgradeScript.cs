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

                # Directories only, and only ones that look like a version: the tree also carries
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

        // winget never tells an MSI which scope to install for. A dual-scope MSI whose default is a
        // per-user install (PdfCraft 0.19.0: MSIINSTALLPERUSER=1, while its winget manifest says
        // Scope: machine) therefore refuses to upgrade the copy installed for every user, and winget
        // reports 0x8A150049. That code proves the installer is an MSI, so only then, and only when
        // Windows Installer holds this application per-machine (under UserData\S-1-5-18), is the
        // upgrade run once more with ALLUSERS=1, which is the scope the installed copy already has.
        // An upgrade that works first time is untouched, and an EXE installer never sees the property.
        const string perMachineMsi = """
            # Windows Installer's "packed" spelling of a product code: the first three groups reversed,
            # the last two with each pair of characters swapped. {39F7F98F-3A8C-43B4-B6C3-778FB442B040}
            # is F89F7F93C8A34B346B3C77F84B240B04.
            function ConvertTo-PackedProductCode {
                param([string] $ProductCode)
                if ($ProductCode -notmatch '^\{([0-9A-Fa-f]{8})-([0-9A-Fa-f]{4})-([0-9A-Fa-f]{4})-([0-9A-Fa-f]{4})-([0-9A-Fa-f]{12})\}$') { return $null }
                $groups = @($Matches[1], $Matches[2], $Matches[3], $Matches[4], $Matches[5])
                $packed = ''
                for ($g = 0; $g -lt 3; $g++) {
                    $chars = $groups[$g].ToCharArray()
                    [array]::Reverse($chars)
                    $packed += -join $chars
                }
                foreach ($group in $groups[3..4]) {
                    for ($i = 0; $i -lt $group.Length; $i += 2) { $packed += $group.Substring($i + 1, 1) + $group.Substring($i, 1) }
                }
                return $packed.ToUpperInvariant()
            }

            # Whether an uninstall entry named exactly $Name is a Windows Installer product registered
            # for every user. A per-user MSI also appears under HKLM's uninstall key, so the entry alone
            # does not say; the product's UserData registration under Local System does.
            function Test-PerMachineMsi {
                param([string] $Name)
                $machineProducts = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Installer\UserData\S-1-5-18\Products'
                foreach ($root in @('HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall', 'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall')) {
                    foreach ($entry in @(Get-ChildItem -LiteralPath $root -ErrorAction SilentlyContinue)) {
                        $packed = ConvertTo-PackedProductCode $entry.PSChildName
                        if (-not $packed) { continue }
                        $displayName = [string] $entry.GetValue('DisplayName')
                        if ($displayName.Trim() -ieq $Name.Trim() -and (Test-Path -LiteralPath (Join-Path $machineProducts $packed))) { return $true }
                    }
                }
                return $false
            }
            """;

        // A major upgrade removes the old version first, and Windows Installer removes a product from
        // its own cached copy of the MSI under C:\Windows\Installer. When that copy is gone (a cleanup
        // tool, a disk sweep), the MSI says Error 1714 with System Error 1612 and the upgrade fails as
        // 0x8A150049 on every cycle, forever (LibreOffice 7.6.7.2 to 26.8.3.1). Putting the same version's
        // MSI back in the cache with `msiexec /fv` is the standard repair, and it changes nothing that is
        // installed, so it is the only repair made here. The rule is "diagnose, then retry once; never
        // uninstall": nothing is removed and no Installer or Uninstall key is edited. The old MSI comes from `winget download` on the winget source,
        // which checks the manifest's SHA-256 itself, or, when winget no longer lists that version (it
        // prunes old ones: LibreOffice 7.6.7.2 failed 0x8A150017), from the address Kintsugi reads out of
        // the manifest winget-pkgs held before the removal (RetiredWingetInstallerLookup), checked here
        // against that manifest's SHA-256. Either way it is re-cached only when its signature is
        // valid and its ProductCode is the installed product's uninstall key, so a different product (or
        // a different build under the same version) is never pushed into the cache. Store ids are left
        // alone: they are not MSIs and not in the winget source. Every step is said on stdout, every
        // skip on stderr, so a failure that was not repaired says why.
        const string missingMsiCache = """
            # Error 1714 with System Error 1612 on one line, in winget's output or the installer log: the old
            # version cannot be removed because Windows Installer's cached copy of its MSI is gone.
            function Test-MissingMsiCache {
                $lines = @(@($script:WingetOutput) | ForEach-Object { "$_" })
                if (Test-Path -LiteralPath $installerLog -PathType Leaf) { $lines += @(Get-Content -LiteralPath $installerLog -ErrorAction SilentlyContinue) }
                foreach ($line in $lines) { if ("$line" -match '(?i)\bError 1714\b.*\bSystem Error 1612\b') { return $true } }
                return $false
            }

            # The Windows Installer products (uninstall keys named by a ProductCode) whose DisplayName is exactly $Name.
            function Get-InstalledMsiEntry {
                param([string] $Name)
                foreach ($root in @('HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall', 'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall')) {
                    foreach ($entry in @(Get-ChildItem -LiteralPath $root -ErrorAction SilentlyContinue)) {
                        if (-not (ConvertTo-PackedProductCode $entry.PSChildName)) { continue }
                        if (([string] $entry.GetValue('DisplayName')).Trim() -ine $Name.Trim()) { continue }
                        [pscustomobject]@{ ProductCode = $entry.PSChildName.ToUpperInvariant(); DisplayVersion = ([string] $entry.GetValue('DisplayVersion')).Trim() }
                    }
                }
            }

            # An MSI's own ProductCode, read from its Property table. Every COM object is released, so the file is
            # not held open when msiexec reads it or the download folder is deleted.
            function Get-MsiProductCode {
                param([string] $Path)
                $installer = New-Object -ComObject WindowsInstaller.Installer
                $database = $null; $view = $null; $record = $null
                try {
                    $database = $installer.GetType().InvokeMember('OpenDatabase', 'InvokeMethod', $null, $installer, @($Path, 0))
                    $view = $database.GetType().InvokeMember('OpenView', 'InvokeMethod', $null, $database, @("SELECT Value FROM Property WHERE Property='ProductCode'"))
                    [void] $view.GetType().InvokeMember('Execute', 'InvokeMethod', $null, $view, $null)
                    $record = $view.GetType().InvokeMember('Fetch', 'InvokeMethod', $null, $view, $null)
                    if (-not $record) { return $null }
                    return [string] $record.GetType().InvokeMember('StringData', 'GetProperty', $null, $record, @(1))
                } finally {
                    if ($view) { [void] $view.GetType().InvokeMember('Close', 'InvokeMethod', $null, $view, $null) }
                    foreach ($com in @($record, $view, $database, $installer)) { if ($com) { [void] [Runtime.InteropServices.Marshal]::ReleaseComObject($com) } }
                }
            }

            # Runs msiexec and waits for it. The caller quotes any path in $Arguments.
            function Invoke-Msiexec {
                param([string[]] $Arguments)
                $process = Start-Process -FilePath (Join-Path $env:SystemRoot 'System32\msiexec.exe') -ArgumentList $Arguments -Wait -PassThru -WindowStyle Hidden
                return $process.ExitCode
            }

            # Asks Kintsugi where the installed version's MSI can still be fetched once winget no longer lists it (GET
            # /api/agent-packages/retired-winget-installer: the address and SHA-256 from the manifest winget-pkgs held
            # before the version was removed) and downloads it into $Folder. Returns $true only when the file is there and
            # has that SHA-256; its signature and ProductCode are checked by the caller, as for winget's own download.
            # $Why says what failed first, so each line on stderr reads on its own in the failure.
            function Save-RetiredMsiFromKintsugi {
                param([string] $Folder, $Installed, [string] $Why)
                $server = Get-KintsugiServer
                if (-not $server) {
                    [Console]::Error.WriteLine("the old MSI was not re-cached: $Why, and this host's Kintsugi server address could not be read")
                    return $false
                }
                # Windows PowerShell 5.1 can leave TLS 1.2 out of its defaults, which the server and the vendor require.
                [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
                $query = 'id=' + [uri]::EscapeDataString($AppId) + '&version=' + [uri]::EscapeDataString($Installed.DisplayVersion) + '&productCode=' + [uri]::EscapeDataString($Installed.ProductCode)
                try {
                    $offer = Invoke-RestMethod -UseBasicParsing -ErrorAction Stop -Uri "$server/api/agent-packages/retired-winget-installer?$query"
                } catch {
                    [Console]::Error.WriteLine("the old MSI was not re-cached: $Why, and Kintsugi knows no other source for it ($($_.Exception.Message))")
                    return $false
                }
                $fields = @{}
                foreach ($name in @('url', 'sha256', 'packageIdentifier', 'manifestCommit')) {
                    $property = $offer.PSObject.Properties[$name]
                    $fields[$name] = if ($property) { [string] $property.Value } else { '' }
                }
                if ($fields.url -notmatch '^https://[^\s/?#]+/[^\s?#]+\.msi$' -or $fields.sha256 -notmatch '^[0-9A-Fa-f]{64}$') {
                    [Console]::Error.WriteLine("the old MSI was not re-cached: $Why, and Kintsugi's answer is not an https .msi address with a SHA-256")
                    return $false
                }
                # The vendor's own address, as winget itself would use; never GitHub, which hosts take no binary from.
                if (([uri] $fields.url).Host -match '(^|\.)github\.com$|githubusercontent\.com$') {
                    [Console]::Error.WriteLine("the old MSI was not re-cached: $Why, and the only other source is on GitHub, which this host never downloads from")
                    return $false
                }
                $target = Join-Path $Folder ([IO.Path]::GetFileName(([uri] $fields.url).AbsolutePath))
                [Console]::Out.WriteLine("downloading $($fields.url), which $($fields.packageIdentifier)'s manifest at winget-pkgs $($fields.manifestCommit) gives SHA-256 $($fields.sha256)")
                try {
                    $ProgressPreference = 'SilentlyContinue'
                    Invoke-WebRequest -UseBasicParsing -ErrorAction Stop -Uri $fields.url -OutFile $target
                } catch {
                    [Console]::Error.WriteLine("the old MSI was not re-cached: $Why, and downloading $($fields.url) failed ($($_.Exception.Message))")
                    return $false
                }
                $actual = (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash
                if ($actual -ine $fields.sha256) {
                    Remove-Item -LiteralPath $target -Force -ErrorAction SilentlyContinue
                    [Console]::Error.WriteLine("the old MSI was not re-cached: $Why, and $($fields.url) has SHA-256 $actual, not the $($fields.sha256) winget-pkgs recorded")
                    return $false
                }
                return $true
            }

            # Puts the installed version's MSI back in Windows Installer's cache (msiexec /fv), from winget's own
            # download of that version, or Kintsugi's address for it once winget no longer lists it. Returns $true only
            # when it was re-cached. Never uninstalls anything and never touches the Installer or Uninstall registry keys.
            function Repair-MissingMsiCache {
                if (-not (Test-MissingMsiCache)) { return $false }
                [Console]::Out.WriteLine("Windows Installer could not remove the installed $AppName (Error 1714, System Error 1612): its cached copy of the old MSI is gone, so it is re-cached from winget")
                if ($source -ne 'winget') {
                    [Console]::Error.WriteLine("the old MSI was not re-cached: $AppId is not a winget-source package")
                    return $false
                }
                $entries = @(Get-InstalledMsiEntry -Name $AppName)
                if ($entries.Count -ne 1) {
                    [Console]::Error.WriteLine("the old MSI was not re-cached: $($entries.Count) Windows Installer products are installed under the name '$AppName', not exactly one")
                    return $false
                }
                $installed = $entries[0]
                if (-not $installed.DisplayVersion) {
                    [Console]::Error.WriteLine("the old MSI was not re-cached: $AppName $($installed.ProductCode) records no DisplayVersion")
                    return $false
                }

                $work = Join-Path ([IO.Path]::GetTempPath()) ('kintsugi-msicache-' + [guid]::NewGuid().ToString('N'))
                try {
                    New-Item -ItemType Directory -Path $work -Force | Out-Null
                    [Console]::Out.WriteLine("downloading $AppId $($installed.DisplayVersion) from the winget source")
                    $lines = @(& $winget download --exact --id $AppId --version $installed.DisplayVersion --source winget --download-directory $work --accept-package-agreements --accept-source-agreements --disable-interactivity)
                    $downloadCode = $LASTEXITCODE
                    foreach ($line in $lines) { [Console]::Out.WriteLine($line) }
                    if ($downloadCode -ne 0) {
                        $why = "winget download of $AppId $($installed.DisplayVersion) exited with 0x{0:X8}" -f $downloadCode
                        [Console]::Out.WriteLine("$why, so Kintsugi is asked where that version's MSI can still be fetched")
                        if (-not (Save-RetiredMsiFromKintsugi -Folder $work -Installed $installed -Why $why)) { return $false }
                    }
                    # -Filter '*.msi' alone would also match .msix: a three-letter wildcard matches longer extensions.
                    $msis = @(Get-ChildItem -LiteralPath $work -Recurse -File -Filter '*.msi' -ErrorAction SilentlyContinue | Where-Object { $_.Extension -ieq '.msi' })
                    if ($msis.Count -eq 0) {
                        [Console]::Error.WriteLine("the old MSI was not re-cached: winget's download of $AppId $($installed.DisplayVersion) holds no .msi")
                        return $false
                    }
                    foreach ($msi in $msis) {
                        $signature = Get-AuthenticodeSignature -FilePath $msi.FullName
                        if ("$($signature.Status)" -ne 'Valid') {
                            [Console]::Error.WriteLine("the old MSI was not re-cached: $($msi.Name) has no valid signature (status $($signature.Status))")
                            continue
                        }
                        $productCode = "$(Get-MsiProductCode -Path $msi.FullName)".Trim()
                        if ($productCode -ine $installed.ProductCode) {
                            [Console]::Error.WriteLine("the old MSI was not re-cached: $($msi.Name) is product '$productCode', not the installed $($installed.ProductCode)")
                            continue
                        }
                        [Console]::Out.WriteLine("re-caching $($msi.Name) ($productCode) with msiexec /fv")
                        $exitCode = Invoke-Msiexec -Arguments @('/fv', ('"' + $msi.FullName + '"'), '/qn', '/norestart')
                        if ($exitCode -eq 0 -or $exitCode -eq 3010) {
                            [Console]::Out.WriteLine("re-cached $AppName $($installed.DisplayVersion) (msiexec exited with $exitCode)")
                            return $true
                        }
                        [Console]::Error.WriteLine("the old MSI was not re-cached: msiexec /fv exited with $exitCode")
                        return $false
                    }
                    return $false
                } catch {
                    [Console]::Error.WriteLine("the old MSI was not re-cached: $($_.Exception.Message)")
                    return $false
                } finally {
                    Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
                }
            }

            # A failed upgrade whose old MSI was re-cached is run once more, with the same extra arguments as the attempt
            # that just failed (ALLUSERS=1 after the per-machine retry), or a dual-scope MSI would refuse it again;
            # anything else keeps its code.
            function Invoke-UpgradeAfterMsiRecache {
                param([int] $Code)
                if ($Code -eq 0 -or $Code -eq -1978335189) { return $Code }
                if (-not (Repair-MissingMsiCache)) { return $Code }
                [Console]::Out.WriteLine('the upgrade is run once more now that the old version can be removed')
                return (Invoke-WingetUpgrade -Extra $script:WingetLastExtra)
            }
            """;

        // A failure inside the vendor's installer reached the server as "winget upgrade exited with
        // 0x8A150049" and nothing else (LibreOffice 7.6 to 26.8), so nobody, and no rule, could say why.
        // winget prints the installer's own exit code, and --log hands the installer a log path where it
        // supports one (an MSI's /log, Inno Setup's /LOG); both are read back on failure and their telling
        // lines put in front of the summary (Get-InstallerEvidence). The summary stays the last line,
        // because the agent keeps the tail of a long failure.
        //
        // One retry is made here, for the one cause that is over in minutes: another installation holding
        // Windows Installer (0x8A150102, or an installer that exited 1618). The script waits for the
        // installer to go idle and runs the upgrade once more. A second is made only after re-caching a
        // missing old MSI (missingMsiCache above). Everything else is left to the agent's own
        // retry schedule (retry_state.rs: 15 minutes, an hour, four hours), which already covers a restart
        // or an application that was open.
        const string updateCommand = """
            # Runs winget, echoes what it printed (the agent logs stdout) and keeps it for Get-InstallerEvidence. Its
            # extra arguments are kept too, for the retry after a re-cache (Invoke-UpgradeAfterMsiRecache).
            function Invoke-WingetUpgrade {
                param([string[]] $Extra)
                $script:WingetLastExtra = if ($Extra) { @($Extra) } else { @() }
                $arguments = @('upgrade', '--exact', '--id', $AppId, '--source', $source, '--silent', '--accept-package-agreements', '--accept-source-agreements', '--disable-interactivity', '--log', $installerLog)
                if ($Extra) { $arguments += $Extra }
                $lines = @(& $winget @arguments)
                $exitCode = $LASTEXITCODE
                foreach ($line in $lines) { [Console]::Out.WriteLine($line) }
                $script:WingetOutput += $lines
                return $exitCode
            }

            # Whether Windows Installer is busy with another installation: its global mutex exists while one runs.
            # A mutex that exists but cannot be opened is busy too.
            function Test-InstallerBusy {
                $mutex = $null
                try {
                    if ([System.Threading.Mutex]::TryOpenExisting('Global\_MSIExecute', [ref] $mutex)) { $mutex.Dispose(); return $true }
                    return $false
                } catch {
                    return $true
                }
            }

            # The installer's own exit code as winget last printed it, or $null.
            function Get-InstallerExitCode {
                $found = $null
                foreach ($line in @($script:WingetOutput)) {
                    if ("$line" -match '(?i)installer failed with exit code:?\s*(-?\d+)') { $found = [int64] $Matches[1] }
                }
                return $found
            }

            # The lines that say why an installer failed: winget's own account of it, then the installer log's errors
            # and failed actions. At most a dozen short lines, so the reason survives the agent's 4000-byte cut.
            function Get-InstallerEvidence {
                $evidence = @(@($script:WingetOutput) | Where-Object { "$_" -match '(?i)exit code|failed|error|abandoned|in use|restart' } | ForEach-Object { "$_".Trim() } | Where-Object { $_ } | Select-Object -Last 4)
                if (Test-Path -LiteralPath $installerLog -PathType Leaf) {
                    $evidence += @(Get-Content -LiteralPath $installerLog -ErrorAction SilentlyContinue |
                        Where-Object { $_ -match '(?i)\bError \d{4}\b|Return value 3|MainEngineThread is returning [1-9]|-- (Error|Installation (operation )?failed)|Exit code:? *[1-9]' } |
                        Select-Object -Last 8)
                }
                return @($evidence | ForEach-Object { $text = "$_".Trim(); if ($text.Length -gt 300) { $text.Substring(0, 300) } else { $text } })
            }

            $source = Get-PackageSource -Id $AppId
            $installerLog = Join-Path ([IO.Path]::GetTempPath()) ('kintsugi-winget-' + [guid]::NewGuid().ToString('N') + '.log')
            $script:WingetOutput = @()
            $script:WingetLastExtra = @()
            $code = Invoke-WingetUpgrade
            # 0x8A150049, the MSI refused: see Test-PerMachineMsi.
            if ($code -eq -1978335159 -and (Test-PerMachineMsi -Name $AppName)) {
                [Console]::Out.WriteLine("the installer refused (0x8A150049) and $AppName is installed for every user, so the upgrade is run again with ALLUSERS=1")
                $code = Invoke-WingetUpgrade -Extra @('--custom', 'ALLUSERS=1')
            }
            # 0x8A150102, or ERROR_INSTALL_ALREADY_RUNNING from the installer: wait up to ten minutes for it to finish,
            # then rerun the attempt that met it, ALLUSERS=1 included, or a dual-scope MSI would refuse it again.
            if ($code -eq -1978334974 -or (Get-InstallerExitCode) -eq 1618) {
                $deadline = (Get-Date).AddMinutes(10)
                while ((Test-InstallerBusy) -and (Get-Date) -lt $deadline) { Start-Sleep -Seconds 15 }
                [Console]::Out.WriteLine('another installation was running; the upgrade is run once more now that Windows Installer is free')
                $code = Invoke-WingetUpgrade -Extra $script:WingetLastExtra
            }
            # Error 1714 / System Error 1612, the old version's cached MSI is gone: see Repair-MissingMsiCache.
            $code = Invoke-UpgradeAfterMsiRecache -Code $code
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

        // winget's own row: check App Installer instead of asking winget to upgrade itself, and when it is
        // behind, install Kintsugi's own copy of it, falling back to asking the Microsoft Store.
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
        // Before the Store is asked, a host that is behind installs the copy Kintsugi serves
        // (Install-AppInstallerFromKintsugi). The Store cannot be relied on: a provisioning debloat script
        // removes it, and with no Store nothing ever updated App Installer on those hosts. This script once
        // downloaded the bundle straight from the winget-cli GitHub releases, which was removed because
        // production hosts must not pull binaries from GitHub; now the server does that
        // (AppInstallerMirror, which checks GitHub's SHA-256 of each file) and the host fetches from the
        // server it already trusts, at the address its agent uses. Nothing is installed until each file
        // matches the hash the server lists and every package carries a valid Microsoft Corporation
        // signature, checked here by Windows, so a server can only ever deliver a genuine Microsoft build.
        // The signature, dependency and licence steps are the ones the GitHub version had. A host with no
        // reachable copy (an older server, the mirror not filled yet) falls back to the Store scan below.
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
                # First, because it is what a real host turned out to have: a provisioning debloat script had removed the
                # Store app itself, provisioned package included, and with no Store nothing delivers App Installer whatever
                # the policies say.
                try {
                    if (@(Get-AppxPackage -AllUsers -Name Microsoft.WindowsStore -ErrorAction Stop).Count -eq 0) {
                        $blockers += 'the Microsoft Store app is not installed for any user (removed, for example by a debloat script), so nothing can deliver App Installer'
                    }
                } catch { $blockers += "whether the Microsoft Store app is installed could not be read: $($_.Exception.Message)" }
                $store = 'HKLM:\SOFTWARE\Policies\Microsoft\WindowsStore'
                # Windows honours RemoveWindowsStore on Enterprise and Education only. On Pro it is ignored, and naming it
                # there sent the reader after a value a whole Pro fleet carried while nearly every host updated normally.
                $edition = Get-PolicyValue 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion' 'EditionID'
                if ((Get-PolicyValue $store 'RemoveWindowsStore') -eq 1 -and "$edition" -match '^(Enterprise|Education|IoTEnterprise)') { $blockers += "the Microsoft Store is removed by policy (RemoveWindowsStore=1, honoured on this $edition edition)" }
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

            # The Kintsugi server this host's agent talks to: the agent's own override, then its config.toml, read the way
            # the Windows agent's config.rs reads them. https only, so nobody on the path chooses what arrives.
            function Get-KintsugiServer {
                $url = $env:PATCHING_AGENT_API_BASE_URL
                if (-not $url) {
                    $config = Join-Path $env:ProgramData 'Kintsugi\kintsugi-agent\config.toml'
                    foreach ($line in @(Get-Content -LiteralPath $config -ErrorAction SilentlyContinue)) {
                        if ($line -match '^\s*api_base_url\s*=\s*"([^"]+)"') { $url = $Matches[1]; break }
                    }
                }
                if ($url -and $url.Trim() -match '^https://[^\s/?#]+(/[^\s?#]*)?$') { return $url.Trim().TrimEnd('/') }
                return $null
            }

            function Assert-MicrosoftSigned {
                param([string] $Path)
                $signature = Get-AuthenticodeSignature -FilePath $Path
                $name = [IO.Path]::GetFileName($Path)
                if ($signature.Status -ne 'Valid') { throw "$name has no valid signature (status $($signature.Status))" }
                $subject = if ($signature.SignerCertificate) { $signature.SignerCertificate.Subject } else { '' }
                if ($subject -notmatch '(^|, )O=Microsoft Corporation(,|$)') { throw "$name is signed, but not by Microsoft Corporation ($subject)" }
            }

            # Installs App Installer from the copy Kintsugi serves (GET /api/agent-packages/app-installer). Returns the
            # winget version it provisioned, or $null after saying on stderr why it did not.
            function Install-AppInstallerFromKintsugi {
                param($CliVersion)
                $server = Get-KintsugiServer
                if (-not $server) {
                    [Console]::Error.WriteLine("Kintsugi's copy of App Installer was not used: this host's Kintsugi server address could not be read.")
                    return $null
                }
                try {
                    $offer = Invoke-RestMethod -UseBasicParsing -ErrorAction Stop -Uri "$server/api/agent-packages/app-installer"
                } catch {
                    [Console]::Error.WriteLine("Kintsugi has no copy of App Installer to offer yet: $($_.Exception.Message)")
                    return $null
                }
                $offered = $null
                $versionProperty = $offer.PSObject.Properties['version']
                $offeredText = if ($versionProperty) { [string] $versionProperty.Value } else { '' }
                if (-not [version]::TryParse($offeredText.TrimStart('v'), [ref] $offered)) {
                    [Console]::Error.WriteLine("Kintsugi's copy of App Installer has an unreadable version '$offeredText'.")
                    return $null
                }
                if ($CliVersion -and $offered -le $CliVersion) {
                    [Console]::Error.WriteLine("Kintsugi's copy of App Installer is winget $offered, no newer than this host's $CliVersion.")
                    return $null
                }

                $ProgressPreference = 'SilentlyContinue'
                $work = Join-Path ([IO.Path]::GetTempPath()) ('kintsugi-appinstaller-' + [guid]::NewGuid().ToString('N'))
                New-Item -ItemType Directory -Path $work -Force | Out-Null
                try {
                    # Each file the server lists is fetched into the work folder and must match the SHA-256 listed with it.
                    # A listed name is a bare file name: anything else could place a download outside the folder.
                    $paths = @{}
                    foreach ($role in @('bundle', 'dependencies', 'license')) {
                        $file = @($offer.files) | Where-Object { $_.role -eq $role } | Select-Object -First 1
                        if (-not $file) { throw "the offer lists no $role file" }
                        $name = [string] $file.name
                        if (-not $name -or [IO.Path]::GetFileName($name) -ne $name) { throw "the offer names an unexpected file '$name'" }
                        $target = Join-Path $work $name
                        Invoke-WebRequest -UseBasicParsing -ErrorAction Stop -Uri "$server/api/agent-packages/app-installer/files/$([uri]::EscapeDataString($name))" -OutFile $target
                        if ((Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -ine [string] $file.sha256) { throw "$name does not match the SHA-256 Kintsugi lists for it" }
                        $paths[$role] = $target
                    }

                    # Nothing is installed before every package has been checked.
                    # A 32-bit PowerShell on 64-bit Windows reports x86 here and the real one in ARCHITEW6432.
                    $architecture = if ($env:PROCESSOR_ARCHITEW6432) { $env:PROCESSOR_ARCHITEW6432 } else { $env:PROCESSOR_ARCHITECTURE }
                    $archFolder = switch ($architecture) { 'ARM64' { 'arm64' } 'x86' { 'x86' } default { 'x64' } }
                    $extract = Join-Path $work 'dependencies'
                    # So far the archive matches only a hash the same server listed, and nothing in it is signed yet, so
                    # no entry may unpack outside $extract: an older Expand-Archive follows ..\ in an entry name.
                    $extractRoot = [IO.Path]::GetFullPath($extract).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
                    Add-Type -AssemblyName System.IO.Compression.FileSystem
                    $zip = [IO.Compression.ZipFile]::OpenRead($paths['dependencies'])
                    try {
                        foreach ($entry in $zip.Entries) {
                            $destination = [IO.Path]::GetFullPath([IO.Path]::Combine($extract, $entry.FullName))
                            if (-not $destination.StartsWith($extractRoot, [StringComparison]::OrdinalIgnoreCase)) {
                                throw "the dependencies archive has an entry outside its folder: $($entry.FullName)"
                            }
                        }
                    } finally {
                        $zip.Dispose()
                    }
                    Expand-Archive -LiteralPath $paths['dependencies'] -DestinationPath $extract -Force
                    $dependencies = @(Get-ChildItem -LiteralPath (Join-Path $extract $archFolder) -Filter '*.appx' -File -ErrorAction Stop)
                    if ($dependencies.Count -eq 0) { throw "the dependencies archive has no packages for $archFolder" }
                    Assert-MicrosoftSigned $paths['bundle']
                    foreach ($dependency in $dependencies) { Assert-MicrosoftSigned $dependency.FullName }
                    # The licence is data, not a signed package: it only has to be a well-formed document.
                    $licenseDocument = New-Object System.Xml.XmlDocument
                    $licenseDocument.Load($paths['license'])

                    # Dependencies first, for this architecture. One already present is not a reason to stop: the
                    # bundle below is what decides whether the install worked.
                    foreach ($dependency in $dependencies) {
                        try {
                            Add-AppxProvisionedPackage -Online -PackagePath $dependency.FullName -SkipLicense -ErrorAction Stop | Out-Null
                        } catch {
                            [Console]::Out.WriteLine("dependency $($dependency.Name) was not provisioned (it may already be present): $($_.Exception.Message)")
                        }
                    }
                    Add-AppxProvisionedPackage -Online -PackagePath $paths['bundle'] -LicensePath $paths['license'] -ErrorAction Stop | Out-Null
                    [Console]::Out.WriteLine("installed Kintsugi's copy of App Installer (winget $offered)")
                    return $offered
                } catch {
                    [Console]::Error.WriteLine("Kintsugi's copy of App Installer (winget $offered) was not installed: $($_.Exception.Message)")
                    return $null
                } finally {
                    Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
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
                # Kintsugi's own copy first. Success is decided by reading the versions again, never by the install
                # alone: a copy older than the latest release still leaves the host behind.
                if (Install-AppInstallerFromKintsugi -CliVersion $cli) {
                    $cliAfter = Get-WingetCliVersion
                    $still = Get-RepairReason -PackageVersion (Get-AppInstallerVersion) -CliVersion $cliAfter -LatestVersion $latest -Minimum $MinimumAppInstallerVersion
                    if (-not $still) {
                        [Console]::Out.WriteLine("winget is now $cliAfter")
                        return 0
                    }
                    [Console]::Error.WriteLine("$still after installing Kintsugi's copy of App Installer; the server refreshes its copy from the winget-cli releases, and the next cycle tries again.")
                    return 1
                }

                # No copy from Kintsugi (the reason is on stderr above). Ask Windows to run a Microsoft Store update scan,
                # so the Store delivers the signed package, and say why it may not. This host is still behind, so the
                # row stays failed (exit 1) until a later run finds it current: a slow Store must not read as a patched
                # machine.
                $scan = Invoke-StoreUpdateScan
                $blockers = @(Get-StoreUpdateBlockers)
                $blockerText = if ($blockers.Count -gt 0) { 'Possible Store blockers on this host: ' + ($blockers -join '; ') + '.' } else { 'No Store blocker was found on this host.' }
                [Console]::Error.WriteLine("$reason. $scan. $blockerText Kintsugi's copy of App Installer was not installed (see above), so the Microsoft Store has to deliver the update; this host stays failed until it is on the latest version.")
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
            # (as Get-AppxPackage reports it, e.g. 1.25.390.0, not the `winget --version` text). The
            # default, 1.27.0, is a starting point: edit it here and re-sign the script to move it.
            $MinimumAppInstallerVersion = [version] '1.27.0'

            $KnownExitCodes = @{
            {{knownCodes}}
            }

            {{exitCodeText}}

            {{latestVersionLogic}}

            {{wingetResolution}}

            {{packageSource}}

            {{perMachineMsi}}

            {{missingMsiCache}}

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
            # 0x8A15002B ("no applicable upgrade found") is winget's way of saying the package is
            # already current. That is the idempotent-success case this contract requires, not a
            # failure.
            if ($code -eq 0 -or $code -eq -1978335189) {
                Remove-Item -LiteralPath $installerLog -Force -ErrorAction SilentlyContinue
                exit 0
            }
            foreach ($line in @(Get-InstallerEvidence)) { [Console]::Error.WriteLine($line) }
            Remove-Item -LiteralPath $installerLog -Force -ErrorAction SilentlyContinue
            $installerCode = Get-InstallerExitCode
            $installerText = if ($null -ne $installerCode) { "; the installer itself exited with $installerCode" } else { '' }
            [Console]::Error.WriteLine("winget upgrade exited with $(Get-ExitCodeText $code)$installerText")
            exit 1
            """;
    }
}
