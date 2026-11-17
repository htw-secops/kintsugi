using System.Diagnostics;
using Kintsugi.Application.UpgradePaths;

namespace Kintsugi.Tests.Application.UpgradePaths;

/// <summary>
/// The scripts from the review of the download check (htw-secops/kintsugi#68). Each of these slipped past the
/// first version, which read the whole text with patterns instead of reading it statement by statement; they are
/// here as the exact scripts, so a change that lets one through again fails.
/// </summary>
public class ScriptDownloadCheckReviewTests
{
    private const string Pkg = "installer -pkg /tmp/app.pkg -target /";

    private static DownloadVerification Check(string script, ScriptLanguage language) =>
        ScriptDownloadCheck.Analyze(script, language).Verification;

    public static TheoryData<string, ScriptLanguage, string> MustBeUnverified => new()
    {
        // A hash that is only printed is not compared, however many other comparisons the script has.
        { "bash: hash printed, an unrelated == elsewhere", ScriptLanguage.Bash,
            "if [[ \"$1\" == \"--update\" ]]; then :; fi\ncurl -o /tmp/app.pkg \"$url\"\nshasum -a 256 /tmp/app.pkg\n" + Pkg },
        { "powershell: hash written out, an unrelated -ne elsewhere", ScriptLanguage.PowerShell,
            "$out = Join-Path $env:TEMP 'app.msi'\nInvoke-WebRequest -Uri $url -OutFile $out\nWrite-Output \"SHA256: $((Get-FileHash $out).Hash)\"\n"
            + "$p = Start-Process msiexec.exe -ArgumentList \"/i `\"$out`\" /qn\" -Wait -PassThru\nif ($p.ExitCode -ne 0) { exit 1 }" },

        // The most common download idioms.
        { "curl redirected to a file, then installed", ScriptLanguage.Bash, "curl -fsSL \"$url\" > /tmp/app.pkg\n" + Pkg },
        { "bash -c with a command substitution", ScriptLanguage.Bash, "/bin/bash -c \"$(curl -fsSL https://example.com/install.sh)\"" },
        { "curl piped to bash", ScriptLanguage.Bash, "curl -fsSL https://example.com/i.sh | /bin/bash" },
        { "curl piped to sudo -E bash", ScriptLanguage.Bash, "curl -fsSL https://example.com/i.sh | sudo -E bash" },
        { "curl piped to python3 -", ScriptLanguage.Bash, "curl -fsSL https://example.com/i.py | python3 -" },
        { "curl piped to tar, then the extracted binary run", ScriptLanguage.Bash, "curl -fsSL \"$url\" | tar -xz -C /opt\n/opt/app/bin/app --install" },
        { "process substitution", ScriptLanguage.Bash, "sh <(curl -fsSL https://example.com/i.sh)" },
        { "splatted Invoke-WebRequest", ScriptLanguage.PowerShell, "$a = @{ Uri = $url; OutFile = $out }\nInvoke-WebRequest @a\nStart-Process $out -Wait" },
        { "iex of a DownloadString", ScriptLanguage.PowerShell, "iex ((New-Object Net.WebClient).DownloadString('https://example.com/i.ps1'))" },

        // The run in a shape the first version did not see.
        { "fetch and run on one line", ScriptLanguage.Bash, "curl -o \"$tmp\" \"$url\" && installer -pkg \"$tmp\" -target /" },
        { "apt-get install -y", ScriptLanguage.Bash, "curl -o \"$deb\" \"$url\"\napt-get install -y \"$deb\"" },
        { "sudo bash on the saved script", ScriptLanguage.Bash, "curl -o /tmp/inst.sh \"$url\"\nsudo bash /tmp/inst.sh" },
        { "tar then the extracted installer", ScriptLanguage.Bash, "curl -o /tmp/app.tgz \"$url\"\ntar -xzf /tmp/app.tgz -C /opt; /opt/app/install" },

        // A host that is a variable used to throw.
        { "host is a variable, curl -O", ScriptLanguage.Bash, "curl -fsSLO \"https://$HOST/app.dmg\"\nhdiutil attach app.dmg" },
        { "host is a variable, wget", ScriptLanguage.Bash, "wget \"https://${MIRROR}/app.run\"\nsh app.run" },

        // A word that says "check" without checking.
        { "check named in a trailing comment", ScriptLanguage.Bash, "curl -o /tmp/app.pkg \"$url\"\ninstaller -pkg /tmp/app.pkg -target / # TODO pkgutil --check-signature" },
        { "check named in a printed string", ScriptLanguage.Bash, "curl -o /tmp/app.pkg \"$url\"\necho 'skipping pkgutil --check-signature'\n" + Pkg },
        { "codesign -dv only displays", ScriptLanguage.Bash, "curl -o /tmp/app.zip \"$url\"\nunzip /tmp/app.zip -d /Applications\ncodesign -dv /Applications/App.app" },
        { "a hash stored in a variable that is never compared", ScriptLanguage.Bash,
            "curl -o /tmp/app.pkg \"$url\"\nactual=$(shasum -a 256 /tmp/app.pkg)\n" + Pkg },
    };

    [Theory]
    [MemberData(nameof(MustBeUnverified))]
    public void AScriptThatDownloadsAndRunsWithoutAnyRealCheck_IsUnverified(string name, ScriptLanguage language, string script)
    {
        _ = name;

        Assert.Equal(DownloadVerification.Unverified, Check(script, language));
    }

    public static TheoryData<string, ScriptLanguage, string> MustNotBeUnverified => new()
    {
        { "a version file read with curl.exe", ScriptLanguage.PowerShell,
            "curl.exe -o $env:TEMP\\versions.json $api\n$v = Get-Content $env:TEMP\\versions.json | ConvertFrom-Json" },
        { "a JSON file tested with [ -s ] && echo", ScriptLanguage.Bash, "curl -o /tmp/latest.json \"$api\"\n[ -s /tmp/latest.json ] && echo ok" },
        { "a JSON file, then || sh -c", ScriptLanguage.Bash, "curl -o /tmp/v.json \"$api\" || sh -c 'echo failed'" },
        { "a version read into a variable", ScriptLanguage.Bash, "latest=$(curl -fsSL https://example.com/latest.txt)\necho \"$latest\"" },
        { "a body thrown away", ScriptLanguage.Bash, "curl -fsSL -o /dev/null https://example.com/ping\ncurl -s https://example.com/ping > /dev/null" },
    };

    [Theory]
    [MemberData(nameof(MustNotBeUnverified))]
    public void AScriptThatOnlyReadsText_IsNotFlagged(string name, ScriptLanguage language, string script)
    {
        _ = name;

        Assert.NotEqual(DownloadVerification.Unverified, Check(script, language));
    }

    public static TheoryData<string, ScriptLanguage, string> RealChecks => new()
    {
        { "hash stored in a variable, then compared", ScriptLanguage.Bash,
            "curl -o /tmp/app.pkg \"$url\"\nactual=$(shasum -a 256 /tmp/app.pkg | cut -d' ' -f1)\nif [ \"$actual\" != \"$EXPECTED\" ]; then exit 1; fi\n" + Pkg },
        { "hash compared inside the test", ScriptLanguage.Bash,
            "curl -o /tmp/a.deb \"$url\"\nif [ \"$(sha256sum /tmp/a.deb | cut -d' ' -f1)\" != \"$SHA\" ]; then exit 1; fi\napt-get install -y /tmp/a.deb" },
        { "echo piped into shasum -c", ScriptLanguage.Bash,
            "curl -fsSL \"$url\" > /tmp/app.pkg\necho \"$SHA  /tmp/app.pkg\" | shasum -a 256 -c -\n" + Pkg },
        { "sha256sum --check", ScriptLanguage.Bash, "curl -o /tmp/a.deb \"$url\"\nsha256sum --check sums.txt\ndpkg -i /tmp/a.deb" },
        { "pkgutil in an && chain", ScriptLanguage.Bash, "curl -o /tmp/app.pkg \"$url\" && pkgutil --check-signature /tmp/app.pkg && installer -pkg /tmp/app.pkg -target /" },
        { "codesign --verify", ScriptLanguage.Bash, "curl -o /tmp/app.zip \"$url\"\nunzip /tmp/app.zip -d /Applications\ncodesign --verify --deep --strict /Applications/App.app" },
        { "gpg --verify in an if", ScriptLanguage.Bash, "curl -o /tmp/a.deb \"$url\"\nif ! gpg --verify /tmp/a.sig /tmp/a.deb; then exit 1; fi\ndpkg -i /tmp/a.deb" },
        { "Get-FileHash compared in the same statement", ScriptLanguage.PowerShell,
            "Invoke-WebRequest -Uri $url -OutFile $out\nif ((Get-FileHash $out -Algorithm SHA256).Hash -ne $expected) { throw 'bad' }\nStart-Process $out -Wait" },
        { "Get-FileHash stored, then compared", ScriptLanguage.PowerShell,
            "Invoke-WebRequest -Uri $url -OutFile $out\n$hash = (Get-FileHash -Path $out -Algorithm SHA256).Hash\nif ($hash -ne $expected) { throw 'bad' }\nStart-Process $out -Wait" },
        { "Authenticode compared in the same statement", ScriptLanguage.PowerShell,
            "Invoke-WebRequest -Uri $url -OutFile $out\nif ((Get-AuthenticodeSignature $out).Status -ne 'Valid') { throw 'bad' }\nStart-Process $out -Wait" },
    };

    [Theory]
    [MemberData(nameof(RealChecks))]
    public void AnInstallerThatIsReallyChecked_IsVerified(string name, ScriptLanguage language, string script)
    {
        _ = name;

        Assert.Equal(DownloadVerification.Verified, Check(script, language));
    }

    [Fact]
    public void AnOddScriptNeverMakesTheCheckThrow_AndNeverCountsAsNothingToCheck()
    {
        var scripts = new[]
        {
            "echo \"unterminated", "echo 'unterminated", "curl -o", "curl >", "$(", "`", "(((((((((((((((((((((((", ")))))))",
            "curl -o /tmp/a.pkg \"$(\ninstaller -pkg /tmp/a.pkg",
            new string('(', 20000), "x=\"" + new string('$', 5000), "curl -o /tmp/a.pkg url; " + string.Concat(Enumerable.Repeat("$(", 200)),
        };

        foreach (var script in scripts)
        {
            _ = ScriptDownloadCheck.Analyze(script, ScriptLanguage.Bash);
            _ = ScriptDownloadCheck.Analyze(script, ScriptLanguage.PowerShell);
        }
    }

    [Fact]
    public void AHugeLine_DoesNotMakeAnUncheckedInstallerPass()
    {
        // The first version timed out on this and answered "no download", which made the timeout a way to be signed.
        var script = "curl -o /tmp/app.pkg \"$url\"\n" + Pkg + "\nx=\"" + string.Concat(Enumerable.Repeat("curl ", 60000)) + "\"";

        var watch = Stopwatch.StartNew();
        var verdict = Check(script, ScriptLanguage.Bash);

        Assert.Equal(DownloadVerification.Unverified, verdict);
        Assert.True(watch.ElapsedMilliseconds < 5000, $"took {watch.ElapsedMilliseconds} ms");
    }

    [Theory]
    [InlineData("Linux", "sha256sum -c", "Get-FileHash")]
    [InlineData("pm:Flatpak", "sha256sum -c", "pkgutil")]
    [InlineData("macOS", "pkgutil --check-signature", "sha256sum")]
    [InlineData("pm:Homebrew", "codesign --verify", "rpm -K")]
    public void TheAdviceNamesTheToolsOfThePlatform(string platform, string shouldMention, string shouldNotMention)
    {
        var result = ScriptDownloadCheck.Analyze("curl -o /tmp/app.bin \"$url\"\nchmod +x /tmp/app.bin\n/tmp/app.bin", ScriptLanguage.Bash, platform);

        Assert.Equal(DownloadVerification.Unverified, result.Verification);
        Assert.Contains(shouldMention, result.Reason);
        Assert.DoesNotContain(shouldNotMention, result.Reason);
    }

    [Fact]
    public void TheWindowsAdviceNamesPowerShellTools()
    {
        var result = ScriptDownloadCheck.Analyze("Invoke-WebRequest -Uri $u -OutFile $o\nStart-Process $o", ScriptLanguage.PowerShell, "Windows");

        Assert.Contains("Get-FileHash", result.Reason);
        Assert.DoesNotContain("sha256sum", result.Reason);
    }

    [Fact]
    public void WithNoPlatform_TheAdviceCoversBothBashFamilies()
    {
        var result = ScriptDownloadCheck.Analyze("curl -o /tmp/app.bin \"$u\"\nsh /tmp/app.bin", ScriptLanguage.Bash);

        Assert.Contains("sha256sum -c", result.Reason);
        Assert.Contains("pkgutil --check-signature", result.Reason);
    }
}
