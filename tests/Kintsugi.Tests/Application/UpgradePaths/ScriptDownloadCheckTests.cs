using Kintsugi.Application.UpgradePaths;

namespace Kintsugi.Tests.Application.UpgradePaths;

public class ScriptDownloadCheckTests
{
    private static DownloadVerification Check(string script, ScriptLanguage language) =>
        ScriptDownloadCheck.Analyze(script, language).Verification;

    // ---- PowerShell, AI-style scripts ----

    private const string MsiNoCheck = """
        $out = Join-Path $env:TEMP 'app.msi'
        Invoke-WebRequest -Uri $url -OutFile $out
        Start-Process msiexec.exe -ArgumentList "/i `"$out`" /qn" -Wait
        """;

    [Fact]
    public void PowerShell_DownloadAndInstallWithNoCheck_IsUnverified()
    {
        var result = ScriptDownloadCheck.Analyze(MsiNoCheck, ScriptLanguage.PowerShell);

        Assert.Equal(DownloadVerification.Unverified, result.Verification);
        Assert.Contains("runs it without checking it", result.Reason);
    }

    [Fact]
    public void TheReasonNamesWhatIsDownloadedAndExactlyWhatToAdd()
    {
        var result = ScriptDownloadCheck.Analyze(
            "Invoke-WebRequest -Uri https://example.com/app.msi -OutFile $out\nStart-Process msiexec.exe -ArgumentList \"/i $out /qn\" -Wait\n",
            ScriptLanguage.PowerShell);

        Assert.Equal(DownloadVerification.Unverified, result.Verification);
        Assert.Equal(
            "This script downloads https://example.com/app.msi and runs it without checking it. " +
            "Verify it before it can be signed: compare its SHA-256 with the vendor's published value (Get-FileHash, then -eq or -ne against it), " +
            "or check its code signature (Get-AuthenticodeSignature, then test that Status is Valid).",
            result.Reason);
    }

    [Fact]
    public void PowerShell_AFileHashComparedWithTheExpectedValue_IsVerified()
    {
        var script = MsiNoCheck + """

            $actual = (Get-FileHash -Path $out -Algorithm SHA256).Hash
            if ($actual -ne $expected) { throw 'checksum mismatch' }
            """;

        var result = ScriptDownloadCheck.Analyze(script, ScriptLanguage.PowerShell);

        Assert.Equal(DownloadVerification.Verified, result.Verification);
        Assert.Null(result.Reason);
    }

    [Fact]
    public void PowerShell_AFileHashThatIsNeverComparedDoesNotCount()
    {
        var script = MsiNoCheck + "\nGet-FileHash -Path $out | Out-Null\n";

        Assert.Equal(DownloadVerification.Unverified, Check(script, ScriptLanguage.PowerShell));
    }

    [Fact]
    public void PowerShell_AValidAuthenticodeSignatureTest_IsVerified()
    {
        var script = MsiNoCheck + """

            $sig = Get-AuthenticodeSignature -FilePath $out
            if ($sig.Status -ne 'Valid') { throw 'bad signature' }
            """;

        Assert.Equal(DownloadVerification.Verified, Check(script, ScriptLanguage.PowerShell));
    }

    [Fact]
    public void PowerShell_AnAuthenticodeCallThatNeverTestsForValidDoesNotCount()
    {
        var script = MsiNoCheck + "\n$sig = Get-AuthenticodeSignature -FilePath $out\n";

        Assert.Equal(DownloadVerification.Unverified, Check(script, ScriptLanguage.PowerShell));
    }

    [Fact]
    public void PowerShell_AContinuedLineIsOneStatement()
    {
        var script = "Invoke-WebRequest -Uri $url `\n    -OutFile 'C:\\Temp\\setup.exe'\nStart-Process 'C:\\Temp\\setup.exe' -Wait\n";

        Assert.Equal(DownloadVerification.Unverified, Check(script, ScriptLanguage.PowerShell));
    }

    [Fact]
    public void PowerShell_BitsTransferOfAnInstaller_IsUnverified()
    {
        var script = "Start-BitsTransfer -Source $url -Destination C:\\Temp\\app.exe\nStart-Process C:\\Temp\\app.exe -Wait\n";

        Assert.Equal(DownloadVerification.Unverified, Check(script, ScriptLanguage.PowerShell));
    }

    [Fact]
    public void PowerShell_ReadingJsonWithoutSavingAFile_IsNoDownload()
    {
        var script = "$release = Invoke-RestMethod -Uri 'https://api.github.com/repos/x/y/releases/latest'\nStart-Process notepad.exe\n";

        Assert.Equal(DownloadVerification.NoDownload, Check(script, ScriptLanguage.PowerShell));
    }

    [Fact]
    public void PowerShell_SavingOnlyADataFile_IsNoDownloadEvenIfSomethingElseRuns()
    {
        var script = "Invoke-WebRequest -Uri $u -OutFile $env:TEMP\\versions.json\nStart-Process cmd.exe -Wait\n";

        Assert.Equal(DownloadVerification.NoDownload, Check(script, ScriptLanguage.PowerShell));
    }

    [Fact]
    public void PowerShell_InvokeExpressionOnADownload_IsUnverified()
    {
        var result = ScriptDownloadCheck.Analyze("iex (Invoke-WebRequest -Uri https://example.com/install.ps1).Content", ScriptLanguage.PowerShell);

        Assert.Equal(DownloadVerification.Unverified, result.Verification);
        Assert.Contains("straight into a shell", result.Reason);
    }

    [Fact]
    public void ACommentThatMentionsADownloadIsNotADownload()
    {
        var script = "# Invoke-WebRequest -Uri $u -OutFile app.msi\n<# Start-BitsTransfer app.exe #>\nWrite-Output 'nothing'\n";

        Assert.Equal(DownloadVerification.NoDownload, Check(script, ScriptLanguage.PowerShell));
    }

    [Fact]
    public void AnArchiveThatIsOnlyStored_IsNotFlagged()
    {
        var script = "Invoke-WebRequest -Uri https://example.com/data.zip -OutFile C:\\Temp\\data.zip\nWrite-Output 'saved'\n";

        Assert.Equal(DownloadVerification.NoDownload, Check(script, ScriptLanguage.PowerShell));
    }

    [Fact]
    public void AnArchiveThatIsExtractedAndRun_IsFlaggedUntilItIsChecked()
    {
        const string script = "Invoke-WebRequest -Uri https://example.com/app.zip -OutFile C:\\Temp\\app.zip\nExpand-Archive C:\\Temp\\app.zip -DestinationPath C:\\Temp\\app\n";

        Assert.Equal(DownloadVerification.Unverified, Check(script, ScriptLanguage.PowerShell));
    }

    // ---- bash, AI-style scripts ----

    private const string PkgNoCheck = """
        #!/bin/bash
        set -euo pipefail
        curl -fsSL -o /tmp/app.pkg "$url"
        installer -pkg /tmp/app.pkg -target /
        """;

    [Fact]
    public void Bash_DownloadAPkgAndInstallItWithNoCheck_IsUnverified() =>
        Assert.Equal(DownloadVerification.Unverified, Check(PkgNoCheck, ScriptLanguage.Bash));

    [Fact]
    public void Bash_AChecksumCompared_IsVerified()
    {
        var script = PkgNoCheck.Replace("installer -pkg", "echo \"$expected  /tmp/app.pkg\" | shasum -a 256 -c -\ninstaller -pkg");

        var result = ScriptDownloadCheck.Analyze(script, ScriptLanguage.Bash);

        Assert.Equal(DownloadVerification.Verified, result.Verification);
        Assert.Null(result.Reason);
    }

    [Fact]
    public void Bash_AChecksumThatIsNeverComparedDoesNotCount()
    {
        var script = PkgNoCheck.Replace("installer -pkg", "shasum -a 256 /tmp/app.pkg\ninstaller -pkg");

        Assert.Equal(DownloadVerification.Unverified, Check(script, ScriptLanguage.Bash));
    }

    [Theory]
    [InlineData("pkgutil --check-signature /tmp/app.pkg")]
    [InlineData("codesign --verify --deep --strict /Applications/App.app")]
    [InlineData("spctl --assess --type install /tmp/app.pkg")]
    public void Bash_ACodeSignatureCheck_IsVerified(string check)
    {
        var script = PkgNoCheck.Replace("installer -pkg", check + "\ninstaller -pkg");

        Assert.Equal(DownloadVerification.Verified, Check(script, ScriptLanguage.Bash));
    }

    [Fact]
    public void Bash_CurlRemoteNameAndHdiutil_IsUnverifiedUntilItIsChecked()
    {
        const string script = "curl -L -O https://example.com/app.dmg\nhdiutil attach app.dmg -nobrowse\n";

        Assert.Equal(DownloadVerification.Unverified, Check(script, ScriptLanguage.Bash));
        Assert.Equal(DownloadVerification.Verified, Check(script + "codesign --verify --deep /Volumes/App/App.app\n", ScriptLanguage.Bash));
    }

    [Fact]
    public void Bash_WgetOfAnInstaller_IsUnverified()
    {
        const string script = "wget -O /tmp/app.deb \"$url\"\ndpkg -i /tmp/app.deb\n";

        Assert.Equal(DownloadVerification.Unverified, Check(script, ScriptLanguage.Bash));
    }

    [Fact]
    public void Bash_PipingACurlIntoAShell_IsUnverified()
    {
        var result = ScriptDownloadCheck.Analyze("curl -fsSL https://example.com/install.sh | sudo bash\n", ScriptLanguage.Bash);

        Assert.Equal(DownloadVerification.Unverified, result.Verification);
        Assert.Contains("straight into a shell", result.Reason);
    }

    [Theory]
    [InlineData("version=$(curl -fsSL https://formulae.brew.sh/api/formula/wget.json)")]
    [InlineData("redirect=$(curl -fsS -o /dev/null -w '%{redirect_url}' https://github.com/x/y/releases/latest)")]
    [InlineData("wget -qO- https://example.com/version.txt")]
    [InlineData("curl -fsSL https://example.com/latest | grep version")]
    public void Bash_ReadingTextOrDiscardingTheBody_IsNoDownload(string line) =>
        Assert.Equal(DownloadVerification.NoDownload, Check(line + "\nbrew upgrade app\n", ScriptLanguage.Bash));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ABlankScriptFetchesNothing(string? script) =>
        Assert.Equal(DownloadVerification.NoDownload, ScriptDownloadCheck.Analyze(script, ScriptLanguage.Bash).Verification);

    // ---- the scripts the server itself writes ----

    [Theory]
    [InlineData(PackageManagerCatalog.Homebrew)]
    [InlineData(PackageManagerCatalog.AppStore)]
    [InlineData(PackageManagerCatalog.Winget)]
    [InlineData(PackageManagerCatalog.Chocolatey)]
    [InlineData(PackageManagerCatalog.Flatpak)]
    [InlineData(PackageManagerCatalog.Snap)]
    public void TheServerWrittenPackageManagerScripts_RaiseNoWarning(string managerName)
    {
        Assert.True(PackageManagerCatalog.TryGet(managerName, out var manager));

        var result = ScriptDownloadCheck.Analyze(manager.BuildScript(), manager.Language);

        // Their version-check code reads text and the install is the package manager's own, which
        // verifies what it fetches. The check must not warn about any of them.
        Assert.NotEqual(DownloadVerification.Unverified, result.Verification);
    }

    [Fact]
    public void AWingetSelfHealThatDownloadsAndChecksAuthenticode_IsVerified()
    {
        // The shape of the winget self-heal in the patching-reliability PR: download the bundle, then
        // require a Valid Authenticode signature before installing it.
        const string script = """
            Invoke-WebRequest -Uri $bundleUrl -OutFile $bundle
            $sig = Get-AuthenticodeSignature -FilePath $bundle
            if ($sig.Status -ne 'Valid' -or $sig.SignerCertificate.Subject -notmatch 'Microsoft Corporation') { throw 'not signed by Microsoft' }
            Add-AppxProvisionedPackage -Online -PackagePath $bundle -LicensePath $license
            """;

        Assert.Equal(DownloadVerification.Verified, Check(script, ScriptLanguage.PowerShell));
    }
}
