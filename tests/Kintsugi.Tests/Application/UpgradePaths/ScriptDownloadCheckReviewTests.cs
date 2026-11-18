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

    // ---- the second review: shapes the statement-by-statement version still let through ----

    public static TheoryData<string, ScriptLanguage, string> SecondReviewMustBeUnverified => new()
    {
        // A run through an argument array: the run names the array, the array names the file. Three of the
        // real approved scripts install exactly like this.
        { "msiexec through an argument array", ScriptLanguage.PowerShell,
            "$msiPath = Join-Path -Path $tempDir -ChildPath 'App.msi'\nInvoke-WebRequest -Uri $url -OutFile $msiPath -UseBasicParsing\n"
            + "$msiArgs = @('/i', $msiPath, '/qn', '/norestart')\n$p = Start-Process -FilePath 'msiexec.exe' -ArgumentList $msiArgs -Wait -PassThru" },
        { "Start-Process through a splat written over several lines", ScriptLanguage.PowerShell,
            "Invoke-WebRequest -Uri $url -OutFile $setup\n$startArgs = @{\n    FilePath = $setup\n    ArgumentList = '/S'\n    Wait = $true\n}\nStart-Process @startArgs" },
        { "installer through a bash array", ScriptLanguage.Bash,
            "curl -fsSL -o \"$PKG\" \"$URL\"\nargs=(-pkg \"$PKG\" -target /)\ninstaller \"${args[@]}\"" },

        // The file run by its own path, made executable with any mode that grants it, or put in place by install.
        { "chmod 755, then run by its path", ScriptLanguage.Bash,
            "curl -fsSL -o /tmp/agent \"https://example.com/dl/agent\"\nchmod 755 /tmp/agent\n/tmp/agent install" },
        { "run by a path built from a variable", ScriptLanguage.Bash,
            "TMP=$(mktemp -d)\ncurl -fsSL -o \"$TMP/setup\" \"$URL\"\nchmod 0755 \"$TMP/setup\"\n\"$TMP/setup\" --silent" },
        { "chmod a+rx, then sudo ./install.sh", ScriptLanguage.Bash,
            "cd \"$TMP\"\ncurl -fsSL -o install.sh \"$URL\"\nchmod a+rx install.sh\nsudo ./install.sh" },
        { "install -m 0755 into the PATH", ScriptLanguage.Bash,
            "TMP=$(mktemp -d)\ncurl -fsSL -o \"$TMP/tool\" \"https://example.com/releases/latest/download/tool-linux-amd64\"\ninstall -m 0755 \"$TMP/tool\" /usr/local/bin/tool" },
        { "${PKG} saved, $PKG installed", ScriptLanguage.Bash, "curl -fsSL -o \"${PKG}\" \"$URL\"\ninstaller -pkg \"$PKG\" -target /" },

        // An apostrophe in a here-document must not open a quote that hides the code after it, and a check
        // named in one is text, not a check.
        { "apostrophe in a usage here-document", ScriptLanguage.Bash,
            "usage() {\n  cat <<USAGE\nUsage: $0 --update. Won't prompt.\nUSAGE\n}\ncurl -fsSL -o /tmp/app.pkg \"$URL\"\ninstaller -pkg /tmp/app.pkg -target /\necho 'done'" },
        { "apostrophe in an error helper's here-document", ScriptLanguage.Bash,
            "die() {\n  cat >&2 <<EOF\nCouldn't update the application.\nEOF\n  exit 1\n}\ncurl -fsSL -o \"$TMP/app.pkg\" \"$URL\" || die\ninstaller -pkg \"$TMP/app.pkg\" -target / || die" },
        { "<<-'EOF' with an apostrophe", ScriptLanguage.Bash,
            "cat <<-'EOF'\n\tIt's done by hand.\n\tEOF\ncurl -fsSL -o /tmp/app.pkg \"$URL\"\ninstaller -pkg /tmp/app.pkg -target /" },
        { "a check named inside a here-document", ScriptLanguage.Bash,
            "cat <<NOTE\nChecks done by hand:\npkgutil --check-signature\nNOTE\ncurl -fsSL -o /tmp/app.pkg \"$URL\"\ninstaller -pkg /tmp/app.pkg -target /" },
        { "an apostrophe in a PowerShell here-string", ScriptLanguage.PowerShell,
            "$help = @'\nIt won't prompt.\n'@\nInvoke-WebRequest -Uri $url -OutFile $out\nStart-Process $out -Wait" },
        { "a here-document fed to bash", ScriptLanguage.Bash, "sudo bash <<'EOF'\ncurl -fsSL https://example.com/i.sh | sh\nEOF" },

        // A hash that is only tested for being there is not compared with anything.
        { "[ -n \"$SUM\" ] on its own", ScriptLanguage.Bash,
            "curl -fsSL -o /tmp/app.pkg \"$URL\"\nSUM=$(shasum -a 256 /tmp/app.pkg | awk '{print $1}')\n[ -n \"$SUM\" ] || { echo \"could not hash\" >&2; exit 1; }\necho \"sha256 $SUM\"\n" + Pkg },
        { "test -n on its own", ScriptLanguage.Bash,
            "curl -fsSL -o /tmp/app.pkg \"$URL\"\nSUM=$(shasum -a 256 /tmp/app.pkg)\ntest -n \"$SUM\"\n" + Pkg },

        // A group is cut into statements like the script around it.
        { "a fetch inside a subshell group", ScriptLanguage.Bash, "(cd /tmp && curl -fsSLO https://example.com/app.pkg)\n" + Pkg },
        { "a run inside a subshell group", ScriptLanguage.Bash,
            "curl -fsSL -o /tmp/app.zip \"$URL\"\n(cd /tmp && unzip -o app.zip && ./App/install.sh)" },
        { "a printed hash and an unrelated == in one group", ScriptLanguage.Bash,
            "(cd /tmp && shasum -a 256 x; [[ \"$1\" == \"--update\" ]] && true)\ncurl -o /tmp/app.pkg \"$url\"\n" + Pkg },

        // A parenthesised argument keeps its text.
        { "-OutFile (Join-Path ...)", ScriptLanguage.PowerShell,
            "Invoke-WebRequest -Uri $url -OutFile (Join-Path $env:TEMP 'app.msi')\nStart-Process msiexec.exe -ArgumentList '/i', (Join-Path $env:TEMP 'app.msi'), '/qn' -Wait" },

        // Wrappers and spellings.
        { "timeout 300 curl", ScriptLanguage.Bash, "timeout 300 curl -fsSL -o /tmp/app.pkg \"$URL\"\n" + Pkg },
        { "a wrapper this check does not know", ScriptLanguage.Bash, "retry 3 curl -fsSL -o /tmp/a.bin \"$URL\"\nchmod +x /tmp/a.bin" },
        { "timeout in front of the run", ScriptLanguage.Bash, "curl -fsSL -o \"$TMP/setup\" \"$URL\"\ntimeout 300 \"$TMP/setup\" --silent" },
        { "curl -o attached to its value", ScriptLanguage.Bash, "curl -fsSL -o/tmp/app.pkg \"$URL\"\n" + Pkg },
        { "curl -fsSLo attached to its value", ScriptLanguage.Bash, "curl -fsSLo/tmp/app.pkg \"$URL\"\n" + Pkg },
        { "bash -c with the pipe inside the string", ScriptLanguage.Bash, "bash -c 'curl -fsSL https://example.com/i.sh | sh'" },

        // Code built from a download.
        { "& ([scriptblock]::Create((irm ...)))", ScriptLanguage.PowerShell,
            "& ([scriptblock]::Create((Invoke-RestMethod https://example.com/install.ps1)))" },
        { "a download kept in a variable, then eval", ScriptLanguage.Bash, "script=$(curl -fsSL https://example.com/install.sh)\neval \"$script\"" },
        { "irm into a variable, then iex", ScriptLanguage.PowerShell, "$s = Invoke-RestMethod $u\niex $s" },

        // Nesting deeper than the parser reads is not dropped.
        { "nested seventeen deep", ScriptLanguage.Bash,
            "x=" + string.Concat(Enumerable.Repeat("$(echo ", 17)) + "$(curl -fsSL https://example.com/i.sh | bash)" + new string(')', 17) },
    };

    [Theory]
    [MemberData(nameof(SecondReviewMustBeUnverified))]
    public void SecondReview_AScriptThatDownloadsAndRunsWithoutAnyRealCheck_IsUnverified(string name, ScriptLanguage language, string script)
    {
        _ = name;

        Assert.Equal(DownloadVerification.Unverified, Check(script, language));
    }

    public static TheoryData<string, ScriptLanguage, string> SecondReviewMustNotBeUnverified => new()
    {
        { "prose in a here-document that names curl | bash", ScriptLanguage.Bash,
            "cat > /etc/motd <<'MSG'\nManaged by Kintsugi. Do not run curl https://x | bash here.\nMSG" },
        { "a here-string <<< is not a here-document", ScriptLanguage.Bash,
            "IFS=. read -ra v1 <<< \"$1\"\ncurl -o /tmp/latest.json \"$api\"\n[ -s /tmp/latest.json ] && echo ok" },
        { "an arithmetic shift is not a here-document", ScriptLanguage.Bash,
            "x=$((1 << 2))\ncurl -o /tmp/latest.json \"$api\"\nlatest=$(jq -r .v /tmp/latest.json)" },
        { "chmod 644 on a downloaded file", ScriptLanguage.Bash, "curl -o /tmp/motd.txt \"$url\"\nchmod 644 /tmp/motd.txt" },
        { "a JSON file read through a variable in PowerShell", ScriptLanguage.PowerShell,
            "Invoke-WebRequest -Uri $api -OutFile $json\n$data = Get-Content $json | ConvertFrom-Json\nif ($data.version -ne $current) { Write-Output 'newer' }" },
        { "a downloaded version piped into python3 -c", ScriptLanguage.Bash,
            "response=$(curl -fsSL \"$api\")\nprintf '%s' \"$response\" | python3 -c 'import json,sys; print(json.load(sys.stdin)[\"v\"])'" },
        { "a JSON file next to a test and a find", ScriptLanguage.Bash,
            "curl -o /tmp/latest.json \"$api\"\n[ -x /usr/sbin/installer ] || exit 1\nfind . -name '*.json'\njq -r .v /tmp/latest.json" },
    };

    [Theory]
    [MemberData(nameof(SecondReviewMustNotBeUnverified))]
    public void SecondReview_AScriptThatOnlyReadsText_IsNotFlagged(string name, ScriptLanguage language, string script)
    {
        _ = name;

        Assert.NotEqual(DownloadVerification.Unverified, Check(script, language));
    }

    public static TheoryData<string, ScriptLanguage, string> SecondReviewRealChecks => new()
    {
        { "PowerShell variables differ only in case", ScriptLanguage.PowerShell,
            "Invoke-WebRequest -Uri $url -OutFile $out\n$Hash = (Get-FileHash -Path $out -Algorithm SHA256).Hash\nif ($hash -ne $expected) { throw 'bad' }\nStart-Process $out -Wait" },
        { "a hash stored with local, compared in the function", ScriptLanguage.Bash,
            "verify() {\n  local actual=$(shasum -a 256 \"$1\" | awk '{print $1}')\n  if [ \"$actual\" != \"$EXPECTED\" ]; then exit 1; fi\n}\ncurl -fsSL -o /tmp/app.pkg \"$URL\"\nverify /tmp/app.pkg\n" + Pkg },
        { "a hash stored with export, then compared", ScriptLanguage.Bash,
            "curl -fsSL -o /tmp/app.pkg \"$URL\"\nexport ACTUAL=$(shasum -a 256 /tmp/app.pkg | awk '{print $1}')\nif [ \"$ACTUAL\" != \"$EXPECTED\" ]; then exit 1; fi\n" + Pkg },
        { "shasum -c inside a subshell group", ScriptLanguage.Bash,
            "curl -fsSL -o \"$dir/app.pkg\" \"$URL\"\n(cd \"$dir\" && shasum -a 256 -c SHA256SUMS)\ninstaller -pkg \"$dir/app.pkg\" -target /" },
        { "pkgutil inside bash -c", ScriptLanguage.Bash,
            "sudo -u \"$u\" bash -c \"curl -o /tmp/a.pkg $URL && pkgutil --check-signature /tmp/a.pkg\"\ninstaller -pkg /tmp/a.pkg -target /" },
    };

    [Theory]
    [MemberData(nameof(SecondReviewRealChecks))]
    public void SecondReview_AnInstallerThatIsReallyChecked_IsVerified(string name, ScriptLanguage language, string script)
    {
        _ = name;

        Assert.Equal(DownloadVerification.Verified, Check(script, language));
    }

    [Fact]
    public void ACheckInAHereDocumentFedToAShell_StillCounts()
    {
        const string script = "curl -fsSL -o /tmp/app.pkg \"$URL\"\nbash <<'EOF'\npkgutil --check-signature /tmp/app.pkg || exit 1\nEOF\n" + Pkg;

        Assert.Equal(DownloadVerification.Verified, Check(script, ScriptLanguage.Bash));
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
