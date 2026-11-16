using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;

namespace Kintsugi.Application.UpgradePaths;

/// <summary>Whether a script fetches an installer and, if it does, whether it checks what it fetched.</summary>
public enum DownloadVerification
{
    /// <summary>The script fetches no installer it then runs: it only reads text (version checks) or runs a package manager.</summary>
    NoDownload,

    /// <summary>The script fetches an installer and checks it (a checksum comparison or a code-signature check).</summary>
    Verified,

    /// <summary>The script fetches a file from the network, runs or installs it, and checks it nowhere.</summary>
    Unverified
}

/// <summary>The verdict and, for <see cref="DownloadVerification.Unverified"/> and
/// <see cref="DownloadVerification.Verified"/>, a sentence saying why. Null for
/// <see cref="DownloadVerification.NoDownload"/>.</summary>
public readonly record struct ScriptDownloadAssessment(DownloadVerification Verification, string? Reason);

/// <summary>
/// Reads a script's text and says whether it downloads something, runs it, and never checks it. A
/// script that does cannot be signed on this server (see <c>SignUpgradePathScriptCommandHandler</c>), and
/// the AI is asked to fix one it writes like that (see <c>AiUpgradePathResearchClient</c>).
/// </summary>
/// <remarks>
/// <para>
/// **This is a heuristic over text, not a proof, and it is deliberately conservative: a false positive
/// makes a script unsignable, so only the clear case is flagged** — a file fetched from the network and
/// then installed or run (an installer type on the line that fetches it, or the saved file named again
/// on a line that runs something), or a download piped straight into a shell, with no hash comparison and
/// no signature check anywhere in the script.
/// </para>
/// <para>
/// What it counts as a check: <c>Get-FileHash</c> with a comparison, <c>Get-AuthenticodeSignature</c> with
/// a test for <c>Valid</c>, <c>shasum</c>/<c>sha256sum</c>/<c>openssl dgst</c>/<c>certutil -hashfile</c>
/// with a comparison, <c>pkgutil --check-signature</c>, <c>codesign --verify</c>, <c>spctl --assess</c>,
/// <c>gpg --verify</c>, <c>rpm -K</c>.
/// </para>
/// <para>
/// Blind spots, both ways. It does not follow variables or function calls, so a fetch through a helper
/// function goes unseen (missed warning). It cannot tell that a check runs BEFORE the installer, that it
/// is applied to the downloaded file, or that the value compared is the vendor's (a check that is present
/// but wrong passes). A check word in the script that is not applied to the download also passes. In the
/// other direction a download whose check is spelled some other way (a custom function, <c>Test-Path</c>
/// on a signature file) is flagged although it is checked: the person fixes that false positive by adding
/// one of the recognised checks to the script. Package-manager installs (winget, brew, apt, choco) are not
/// downloads here: the manager verifies what it fetches.
/// </para>
/// </remarks>
public static class ScriptDownloadCheck
{
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromSeconds(1);

    private const RegexOptions Insensitive = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    private static Regex Rx(string pattern) => new(pattern, Insensitive, MatchTimeout);

    // Case-sensitive on purpose: curl's -o (write to a named file) and -O (name the file after the URL)
    // differ only in case, and so do the shapes of their arguments.
    private static Regex RxExact(string pattern) => new(pattern, RegexOptions.CultureInvariant, MatchTimeout);

    // ---- what fetches a file ----

    // PowerShell: the cmdlets and methods that write a fetched file to disk. Group "target" is the file.
    private static readonly Regex PowerShellOutFile = Rx(
        @"\b(?:Invoke-WebRequest|iwr|Invoke-RestMethod|irm)\b[^\r\n]*?\s-OutFile\s+(?<target>""[^""]+""|'[^']+'|\S+)");

    private static readonly Regex BitsTransfer = Rx(@"\bStart-BitsTransfer\b[^\r\n]*?\s-Destination\s+(?<target>""[^""]+""|'[^']+'|\S+)|\bStart-BitsTransfer\b");

    private static readonly Regex WebClientDownloadFile = Rx(@"\.DownloadFile\s*\([^,]+,\s*(?<target>""[^""]+""|'[^']+'|[^)\s]+)\s*\)");

    // curl: -o <file> / --output <file> (a cluster such as -fsSLo ends in o), and -O / --remote-name.
    private static readonly Regex CurlOutput = RxExact(
        @"\b[cC]url(?:\.exe)?\b[^\r\n]*?(?:^|\s)(?:-[A-Za-z]*o|--output)(?:\s+|=)(?<target>""[^""]+""|'[^']+'|[^\s|;&]+)");

    private static readonly Regex CurlRemoteName = RxExact(@"\b[cC]url(?:\.exe)?\b[^\r\n]*?(?:\s-[A-Za-z]*O[A-Za-z]*(?=\s|$)|\s--remote-name(?:-all)?\b)");

    private static readonly Regex Wget = Rx(@"\bwget(?:\.exe)?\b[^\r\n]*");
    private static readonly Regex WgetStdout = RxExact(@"\s-[A-Za-z]*O\s*-(?=\s|$)|\s--output-document[= ]-(?=\s|$)");
    private static readonly Regex WgetOutput = RxExact(@"\s(?:-[A-Za-z]*O|--output-document)(?:\s+|=)(?<target>[^\s|;&]+)");

    // A download run without ever being saved: nothing can check it before it runs.
    private static readonly Regex PipeIntoShell = Rx(
        @"\b(?:curl(?:\.exe)?|wget|Invoke-WebRequest|iwr|Invoke-RestMethod|irm)\b[^\r\n]*\|\s*(?:sudo\s+)?(?:bash|sh|zsh|pwsh|powershell|iex|Invoke-Expression)\b"
        + @"|\b(?:iex|Invoke-Expression)\b[^\r\n]*\b(?:iwr|irm|Invoke-WebRequest|Invoke-RestMethod|DownloadString)\b");

    private static readonly Regex Url = Rx(@"https?://[^\s""'`)<>|;]+");

    // ---- what makes a download an installer ----

    private static readonly Regex InstallerType = Rx(@"\.(?:exe|msi|msix|msixbundle|appx|appxbundle|pkg|dmg|deb|rpm|run|appimage)\b");

    private static readonly Regex RunsSomething = Rx(
        @"\bmsiexec\b|\bStart-Process\b|\binstaller\s+-pkg\b|\bhdiutil\s+attach\b|\bdpkg\s+-i\b|\brpm\s+-[iU]|\bAdd-Appx(?:Provisioned)?Package\b"
        + @"|\bchmod\s+\+x\b|\bExpand-Archive\b|\bunzip\b|\bapt(?:-get)?\s+install\s+\./|\bdnf\s+install\s+\./|(?:^|[\s;&(])&\s*[\$\.\w""']|^\s*(?:bash|sh)\s");

    // ---- what counts as checking the download ----

    private static readonly Regex AuthenticodeCheck = Rx(@"\bGet-AuthenticodeSignature\b[\s\S]*\bValid\b");
    private static readonly Regex PowerShellHash = Rx(@"\bGet-FileHash\b");
    private static readonly Regex PowerShellCompare = Rx(@"\s-(?:i|c)?(?:eq|ne|match|notmatch|like|notlike)\s");
    private static readonly Regex BashHash = Rx(@"\bshasum\b|\bsha(?:256|512)sum\b|\bopenssl\s+(?:dgst|sha256|sha512)\b|\bcertutil(?:\.exe)?\s+-hashfile\b");
    private static readonly Regex BashCompare = Rx(@"==|!=|=\s*\$|\s-c\b|\bdiff\b|\bcmp\b|\[\[?\s|\btest\b|\bgrep\b");
    private static readonly Regex SignatureTools = Rx(
        @"\bpkgutil\s+--check-signature\b|\bcodesign\s+(?:-v\b|--verify\b|-dv\b)|\bspctl\s+(?:--assess|-a)\b|\bgpg2?\s+--verify\b|\brpm\s+(?:-K|--checksig)\b");

    private const string HowToFix =
        "Verify it before it can be signed: compare its SHA-256 with the vendor's published value (Get-FileHash / shasum -a 256), " +
        "or check its code signature (Get-AuthenticodeSignature, pkgutil --check-signature, codesign --verify).";

    private static readonly ConcurrentDictionary<(ScriptLanguage, string), ScriptDownloadAssessment> Cache = new();
    private const int MaxCached = 512;

    /// <summary>Assesses <paramref name="script"/>. A null or blank script fetches nothing.</summary>
    public static ScriptDownloadAssessment Analyze(string? script, ScriptLanguage language)
    {
        if (string.IsNullOrWhiteSpace(script))
        {
            return new ScriptDownloadAssessment(DownloadVerification.NoDownload, null);
        }

        if (Cache.TryGetValue((language, script), out var cached))
        {
            return cached;
        }

        ScriptDownloadAssessment result;
        try
        {
            result = Assess(script, language);
        }
        catch (RegexMatchTimeoutException)
        {
            // An odd, huge or hostile script must not make a script unsignable by timing out: when the
            // analysis cannot finish, it says nothing.
            result = new ScriptDownloadAssessment(DownloadVerification.NoDownload, null);
        }

        if (Cache.Count >= MaxCached)
        {
            Cache.Clear();
        }

        Cache[(language, script)] = result;
        return result;
    }

    private sealed record Fetch(string Statement, string? Target);

    private static ScriptDownloadAssessment Assess(string script, ScriptLanguage language)
    {
        var code = StripComments(script);
        var statements = JoinContinuations(code, language).ToList();

        string? piped = null;
        var fetches = new List<Fetch>();

        foreach (var statement in statements)
        {
            if (PipeIntoShell.IsMatch(statement))
            {
                piped ??= DescribeSource(statement, null);
                continue;
            }

            var fetch = FindFetch(statement);
            if (fetch is not null)
            {
                fetches.Add(fetch);
            }
        }

        if (piped is not null)
        {
            return new ScriptDownloadAssessment(
                DownloadVerification.Unverified,
                $"This script pipes {piped} straight into a shell, so nothing can check it before it runs. " +
                "Save it to a file first. " + HowToFix);
        }

        var installers = fetches.Where(f => IsRunOrInstalled(f, statements)).ToList();
        if (installers.Count == 0)
        {
            return new ScriptDownloadAssessment(DownloadVerification.NoDownload, null);
        }

        var checks = ChecksFound(code);
        if (checks.Count > 0)
        {
            return new ScriptDownloadAssessment(DownloadVerification.Verified, "Checks the download: " + string.Join(", ", checks) + ".");
        }

        var first = installers[0];
        return new ScriptDownloadAssessment(
            DownloadVerification.Unverified,
            $"This script downloads {DescribeSource(first.Statement, first.Target)} and runs it without checking it. " + HowToFix);
    }

    /// <summary>The file a statement fetches to disk, or null when it fetches none (it reads text, or
    /// throws the body away to /dev/null).</summary>
    private static Fetch? FindFetch(string statement)
    {
        foreach (var rx in new[] { PowerShellOutFile, WebClientDownloadFile })
        {
            var m = rx.Match(statement);
            if (m.Success)
            {
                return IsDiscard(m.Groups["target"].Value) ? null : new Fetch(statement, Clean(m.Groups["target"].Value));
            }
        }

        if (BitsTransfer.IsMatch(statement))
        {
            var m = BitsTransfer.Match(statement);
            return new Fetch(statement, m.Groups["target"].Success ? Clean(m.Groups["target"].Value) : null);
        }

        foreach (Match m in CurlOutput.Matches(statement))
        {
            if (!IsDiscard(m.Groups["target"].Value))
            {
                return new Fetch(statement, Clean(m.Groups["target"].Value));
            }
        }

        if (CurlRemoteName.IsMatch(statement))
        {
            return new Fetch(statement, FileNameOf(Url.Match(statement).Value));
        }

        var wget = Wget.Match(statement);
        if (wget.Success && !WgetStdout.IsMatch(wget.Value))
        {
            var output = WgetOutput.Match(wget.Value);
            return output.Success
                ? (IsDiscard(output.Groups["target"].Value) ? null : new Fetch(statement, Clean(output.Groups["target"].Value)))
                : new Fetch(statement, FileNameOf(Url.Match(statement).Value));
        }

        return null;
    }

    /// <summary>Installed or run: an installer type on the line that fetches it, or the saved file named
    /// again on another line that runs something. A fetch that is neither (a data file, an archive that is
    /// only stored) is not flagged.</summary>
    private static bool IsRunOrInstalled(Fetch fetch, List<string> statements)
    {
        if (InstallerType.IsMatch(fetch.Statement))
        {
            return true;
        }

        if (string.IsNullOrEmpty(fetch.Target))
        {
            return false;
        }

        var name = Path.GetFileName(fetch.Target.Replace('\\', '/'));
        var token = string.IsNullOrEmpty(name) ? fetch.Target : name;
        return statements.Any(s => !ReferenceEquals(s, fetch.Statement)
            && s.Contains(token, StringComparison.OrdinalIgnoreCase)
            && RunsSomething.IsMatch(s));
    }

    private static string DescribeSource(string statement, string? target)
    {
        var url = Url.Match(statement);
        if (url.Success)
        {
            return url.Value.TrimEnd('.', ',', ')');
        }

        return string.IsNullOrEmpty(target) ? "a file" : target;
    }

    private static string? FileNameOf(string url) =>
        string.IsNullOrEmpty(url) ? null : Path.GetFileName(new Uri(url, UriKind.RelativeOrAbsolute).IsAbsoluteUri ? new Uri(url).AbsolutePath : url);

    private static string Clean(string value) => value.Trim().Trim('"', '\'');

    /// <summary>/dev/null, $null, NUL and "-" are where output goes when nobody wants the file.</summary>
    private static bool IsDiscard(string target) => Clean(target) is "-" or "/dev/null" or "NUL" or "nul" or "$null";

    private static List<string> ChecksFound(string code)
    {
        var found = new List<string>();

        if (AuthenticodeCheck.IsMatch(code))
        {
            found.Add("Authenticode signature (Valid)");
        }

        if (PowerShellHash.IsMatch(code) && PowerShellCompare.IsMatch(code))
        {
            found.Add("file hash compared");
        }

        if (BashHash.IsMatch(code) && BashCompare.IsMatch(code))
        {
            found.Add("checksum compared");
        }

        if (SignatureTools.IsMatch(code))
        {
            found.Add("code signature");
        }

        return found;
    }

    private static string StripComments(string script)
    {
        // PowerShell block comments first, then whole-line comments in both languages. A trailing
        // "# ..." after code is left alone: cutting at the first # would also cut a URL fragment.
        var noBlocks = Regex.Replace(script, @"<#[\s\S]*?#>", string.Empty, RegexOptions.None, MatchTimeout);
        return string.Join('\n', noBlocks.Split('\n').Where(line => !line.TrimStart().StartsWith('#')));
    }

    /// <summary>One entry per statement: lines ending in a backtick (PowerShell) or a backslash (bash)
    /// continue on the next line.</summary>
    private static IEnumerable<string> JoinContinuations(string code, ScriptLanguage language)
    {
        var continuation = language == ScriptLanguage.PowerShell ? '`' : '\\';
        var current = new StringBuilder();
        foreach (var raw in code.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var continues = line.TrimEnd().EndsWith(continuation);
            current.Append(continues ? line.TrimEnd().TrimEnd(continuation) : line).Append(' ');
            if (!continues)
            {
                yield return current.ToString();
                current.Clear();
            }
        }

        if (current.Length > 0)
        {
            yield return current.ToString();
        }
    }
}
