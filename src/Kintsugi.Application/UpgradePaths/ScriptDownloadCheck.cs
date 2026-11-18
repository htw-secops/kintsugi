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

    /// <summary>The script fetches a file from the network and runs or installs it without checking it, or could
    /// not be read well enough to say that it does not.</summary>
    Unverified
}

/// <summary>The verdict and, for <see cref="DownloadVerification.Unverified"/>, a sentence saying why and what to add.
/// Null otherwise.</summary>
public readonly record struct ScriptDownloadAssessment(DownloadVerification Verification, string? Reason);

/// <summary>
/// Reads a script's text and says whether it downloads something, runs it, and never checks it. A
/// script that does cannot be signed on this server (see <c>SignUpgradePathScriptCommandHandler</c>), and
/// the AI is asked to fix one it writes like that (see <c>AiUpgradePathResearchClient</c>).
/// </summary>
/// <remarks>
/// <para>
/// **This is a heuristic over text, not a proof.** It reads a script the way a shell does, not with one
/// pattern over the whole text: here-document and here-string bodies are taken out first and read on their own
/// (see <see cref="HereDocuments"/>), comments are removed (a trailing <c>#</c> too), the script is cut into
/// statements at newlines, <c>;</c>, <c>&amp;&amp;</c> and <c>||</c> outside quotes, each statement into
/// pipeline segments and words, and a command substitution, parenthesised group or <c>bash -c '...'</c> program
/// is read as statements of its own. What a statement does is decided from its command words (behind
/// <c>sudo</c>, <c>timeout</c> and the like), never from a word that happens to appear somewhere (in a string it
/// prints, in a comment, as the extension of <c>curl.exe</c>).
/// </para>
/// <para>
/// It flags a file fetched from the network (<c>curl</c> to a file or a redirect, <c>wget</c>,
/// <c>Invoke-WebRequest</c> straight or splatted, BITS, <c>DownloadFile</c>, <c>certutil -urlcache</c>) and then
/// installed or run (an installer type, or the saved file named again by a command that runs something, in
/// the same line or a later one, directly or through a variable assigned from it, or run by its own path), a
/// download unpacked as it streams in (<c>curl | tar -x</c>), and a download
/// run without ever being saved (<c>curl | bash</c>, <c>bash -c "$(curl ...)"</c>, <c>iex (irm ...)</c>,
/// <c>[scriptblock]::Create((irm ...))</c>, or kept in a variable that is then run as code), when nothing in the
/// script checks it.
/// </para>
/// <para>
/// What counts as a check, in a statement that does it rather than prints it: a hash tool
/// (<c>Get-FileHash</c>, <c>shasum</c>, <c>sha256sum</c>, <c>openssl dgst</c>, <c>certutil -hashfile</c>) used with
/// <c>-c</c> or in the same statement as a comparison, or whose result is stored in a variable that a later
/// statement compares; <c>Get-AuthenticodeSignature</c> tested for <c>Valid</c> the same way;
/// <c>pkgutil --check-signature</c>, <c>codesign --verify</c> (not <c>-d</c>, which only displays),
/// <c>spctl --assess</c>, <c>gpg --verify</c>, <c>rpm -K</c>. A hash that is only printed is not a check, nor is
/// one that is only tested for being there (<c>[ -n "$SUM" ]</c>), nor one named in a here-document that is
/// not fed to a shell.
/// </para>
/// <para>
/// **It fails closed.** A script it cannot read (the analysis throws or times out, or nests deeper than it reads) is
/// <see cref="DownloadVerification.Unverified"/>, never <see cref="DownloadVerification.NoDownload"/>: a
/// timeout must not be a way to get a script signed.
/// </para>
/// <para>
/// Blind spots remain. It follows a variable only when it is assigned from text that names the saved file,
/// and it does not follow function calls, so a fetch through a helper function goes unseen. A run behind a
/// wrapper it does not know is seen only when the command behind it is one it knows (<c>retry 3 installer</c>,
/// not <c>retry 3 "$TMP/setup"</c>). It cannot tell that a check runs BEFORE the installer, that it is applied to the
/// downloaded file, or that the value compared is the vendor's (a check that is present but wrong passes). In
/// the other direction a download whose check is spelled some other way (a custom function, <c>Test-Path</c>
/// on a signature file) is flagged although it is checked: the person fixes that by adding one of the
/// recognised checks. Package-manager installs (winget, brew, apt, choco) are not downloads here: the
/// manager verifies what it fetches. It looks only at what is signed through the Script path; a package
/// manager <c>Command</c> row is signed without this check.
/// </para>
/// </remarks>
public static class ScriptDownloadCheck
{
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromSeconds(1);

    private const RegexOptions Insensitive = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    /// <summary>Assesses <paramref name="script"/>. A null or blank script fetches nothing.</summary>
    /// <param name="platform">The platform bucket the script is stored under, only so the advice names the
    /// tools of that platform (<c>sha256sum -c</c> on Linux, <c>pkgutil</c> on macOS, <c>Get-FileHash</c> on
    /// Windows). Null gives the advice for the language alone.</param>
    public static ScriptDownloadAssessment Analyze(string? script, ScriptLanguage language, string? platform = null)
    {
        if (string.IsNullOrWhiteSpace(script))
        {
            return new ScriptDownloadAssessment(DownloadVerification.NoDownload, null);
        }

        try
        {
            return Assess(script, language, platform);
        }
        catch (Exception)
        {
            // Includes a regex timeout. A script this check cannot read is not a script it can vouch for:
            // failing open here would make an odd, huge or hostile script the easiest one to sign.
            return new ScriptDownloadAssessment(
                DownloadVerification.Unverified,
                "This script could not be analysed (it is too large or too unusual for the download check), so it cannot be treated as safe to sign. "
                + HowToFix(language, platform));
        }
    }

    // ---- the words the analysis recognises ----

    private static readonly HashSet<string> FetchCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "curl", "wget", "invoke-webrequest", "iwr", "invoke-restmethod", "irm", "start-bitstransfer"
    };

    private static readonly HashSet<string> Interpreters = new(StringComparer.OrdinalIgnoreCase)
    {
        "bash", "sh", "zsh", "dash", "ksh", "python", "python3", "perl", "ruby", "node", "pwsh", "powershell",
        "iex", "invoke-expression", "eval", "source", "."
    };

    private static readonly HashSet<string> PrintCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "echo", "printf", "write-host", "write-output", "write-warning", "write-error", "write-verbose",
        "write-debug", "write-information", "print"
    };

    private static readonly HashSet<string> CommandPrefixes = new(StringComparer.OrdinalIgnoreCase)
    {
        "sudo", "env", "time", "nohup", "command", "exec", "builtin", "if", "!", "then", "else", "elif", "do",
        "while", "until", "{", "}", "&", "elseif", "foreach", "for", "try", "catch", "finally"
    };

    /// <summary>sudo options that take a value, so the value is not mistaken for the command.</summary>
    private static readonly HashSet<string> SudoOptionsWithValue = new(StringComparer.Ordinal)
    {
        "-u", "-g", "-h", "-p", "-r", "-t", "-C", "-D", "-U"
    };

    private static readonly Regex InstallerType = new(
        @"\.(?:exe|msi|msix|msixbundle|appx|appxbundle|pkg|dmg|deb|rpm|run|appimage)(?:[""'?#\s)]|$)", Insensitive, MatchTimeout);

    private static readonly Regex ShellVariableAssignment = new(@"^[A-Za-z_]\w*=", RegexOptions.CultureInvariant, MatchTimeout);

    private static readonly Regex SplatTarget = new(
        @"\b(?:OutFile|Destination)\s*=\s*(?<target>""[^""]*""|'[^']*'|[^\s;}]+)", Insensitive, MatchTimeout);

    private static readonly Regex DownloadFileCall = new(
        @"\.DownloadFile\s*\(\s*(?<url>[^,]+),\s*(?<target>""[^""]+""|'[^']+'|[^)\s]+)\s*\)", Insensitive, MatchTimeout);

    private static readonly Regex DownloadStringCall = new(@"\.DownloadString\s*\(", Insensitive, MatchTimeout);

    /// <summary>PowerShell code built from text: <c>[scriptblock]::Create(...)</c>, <c>.InvokeScript(...)</c>,
    /// <c>.NewScriptBlock(...)</c>. What it is built from runs, as with <c>iex</c>.</summary>
    private static readonly Regex ScriptBlockFromText = new(
        @"\[scriptblock\]::create\s*\(|\.InvokeScript\s*\(|\.NewScriptBlock\s*\(", Insensitive, MatchTimeout);

    /// <summary>A variable inside a saved file's name: <c>$PKG</c> or <c>${PKG}</c>.</summary>
    private static readonly Regex VariableReference = new(@"\$\{(?<name>\w+)\}|\$(?<name>\w+)", RegexOptions.CultureInvariant, MatchTimeout);

    private static readonly Regex ValidWord = new(@"\bValid\b", RegexOptions.CultureInvariant, MatchTimeout);

    private static readonly HashSet<string> PowerShellCompareOperators = new(StringComparer.OrdinalIgnoreCase)
    {
        "-eq", "-ne", "-ieq", "-ine", "-ceq", "-cne", "-like", "-notlike", "-ilike", "-clike", "-match", "-notmatch",
        "-imatch", "-cmatch", "-contains", "-notcontains"
    };

    private static readonly HashSet<string> BashCompareOperators = new(StringComparer.Ordinal)
    {
        "==", "!=", "=", "=~", "-eq", "-ne"
    };

    /// <summary>Commands that compare by themselves. Not <c>[</c>, <c>[[</c> or <c>test</c>: those compare only with
    /// one of <see cref="BashCompareOperators"/>, and <c>[ -n "$SUM" ]</c> on its own asks only that a hash exists.</summary>
    private static readonly HashSet<string> BashCompareCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "diff", "cmp", "grep"
    };

    /// <summary>Commands that run the command after them: their own options and numbers are skipped
    /// (<c>timeout 300 curl ...</c>, <c>nice -n 10 ...</c>, <c>arch -arm64 ...</c>).</summary>
    private static readonly HashSet<string> Wrappers = new(StringComparer.OrdinalIgnoreCase)
    {
        "timeout", "gtimeout", "nice", "ionice", "caffeinate", "stdbuf", "xargs", "arch", "chrt", "taskset"
    };

    private static readonly Regex WrapperArgument = new(
        @"^(?:-\S*|\d+(?:\.\d+)?[smhd]?|\$\{?\w+\}?)$", RegexOptions.CultureInvariant, MatchTimeout);

    /// <summary>What may sit between an unknown wrapper and the command it runs: a number or a variable, not an
    /// option, whose value could be any word (<c>spctl --assess --type install</c> does not run install).</summary>
    private static readonly Regex UnknownWrapperArgument = new(
        @"^(?:\d+(?:\.\d+)?[smhd]?|\$\{?\w+\}?)$", RegexOptions.CultureInvariant, MatchTimeout);

    // ---- what the script is made of ----

    private sealed record Token(string Text, bool Quoted);

    private sealed class Stmt
    {
        public string Raw = string.Empty;
        public readonly List<List<Token>> Segments = new() { new List<Token>() };
        public readonly List<Stmt> Nested = new();
        public readonly List<string> Redirects = new();

        /// <summary>One of several statements a subshell group <c>( a &amp;&amp; b )</c> was cut into. It is read as a
        /// statement of its own, never as part of the statement around it (see <see cref="Effective"/>), so a
        /// hash in one and a comparison in another are not "in the same statement".</summary>
        public bool Grouped;
    }

    /// <summary>A download. <paramref name="Content"/>: kept in a variable (<c>x=$(curl ...)</c>,
    /// <c>$s = irm ...</c>) rather than saved, and named by <paramref name="Target"/>, that variable.</summary>
    private sealed record Fetch(Stmt Statement, string? Target, string? Url, bool UnpackedAsItArrives, bool Content = false);

    private static ScriptDownloadAssessment Assess(string script, ScriptLanguage language, string? platform)
    {
        var powerShell = language == ScriptLanguage.PowerShell;
        var everything = new List<Stmt>();
        var checkable = new List<Stmt>();
        var text = new StringBuilder();
        Read(script, powerShell, true, everything, checkable, text, 0);
        var code = text.ToString();

        var piped = everything.Where(PipesIntoAnInterpreter).Select(s => DescribeSource(s, null)).FirstOrDefault();
        if (piped is not null)
        {
            return new ScriptDownloadAssessment(
                DownloadVerification.Unverified,
                $"This script pipes {piped} straight into a shell, so nothing can check it before it runs. Save it to a file first. "
                + HowToFix(language, platform));
        }

        var fetches = FindFetches(everything, code, powerShell);
        var assignments = everything
            .Where(s => !FetchesAnywhere(s))
            .Select(s => (Statement: s, Variable: AssignedVariable(s, powerShell)))
            .Where(a => a.Variable is not null)
            .Select(a => (a.Statement, a.Variable!))
            .ToList();
        var installers = fetches.Where(f => IsRunOrInstalled(f, everything, assignments, powerShell)).ToList();
        if (installers.Count == 0)
        {
            return new ScriptDownloadAssessment(DownloadVerification.NoDownload, null);
        }

        if (HasACheck(checkable, powerShell))
        {
            return new ScriptDownloadAssessment(DownloadVerification.Verified, null);
        }

        var first = installers[0];
        return new ScriptDownloadAssessment(
            DownloadVerification.Unverified,
            $"This script downloads {DescribeSource(first.Statement, first.Target, first.Url)} and runs it without checking it. " + HowToFix(language, platform));
    }

    private static string HowToFix(ScriptLanguage language, string? platform)
    {
        const string Lead = "Verify it before it can be signed: ";
        var p = platform ?? string.Empty;

        if (language == ScriptLanguage.PowerShell || p.Contains("windows", StringComparison.OrdinalIgnoreCase)
            || p.Contains("winget", StringComparison.OrdinalIgnoreCase) || p.Contains("chocolatey", StringComparison.OrdinalIgnoreCase))
        {
            return Lead + "compare its SHA-256 with the vendor's published value (Get-FileHash, then -eq or -ne against it), "
                + "or check its code signature (Get-AuthenticodeSignature, then test that Status is Valid).";
        }

        if (p.Contains("linux", StringComparison.OrdinalIgnoreCase) || p.Contains("flatpak", StringComparison.OrdinalIgnoreCase)
            || p.Contains("snap", StringComparison.OrdinalIgnoreCase))
        {
            return Lead + "compare its SHA-256 with the vendor's published value (sha256sum -c), "
                + "or check its signature (gpg --verify, rpm -K).";
        }

        if (p.Contains("mac", StringComparison.OrdinalIgnoreCase) || p.Contains("homebrew", StringComparison.OrdinalIgnoreCase)
            || p.Contains("app store", StringComparison.OrdinalIgnoreCase))
        {
            return Lead + "compare its SHA-256 with the vendor's published value (shasum -a 256 -c), "
                + "or check its code signature (pkgutil --check-signature, codesign --verify, spctl --assess).";
        }

        return Lead + "compare its SHA-256 with the vendor's published value (shasum -a 256 -c or sha256sum -c), "
            + "or check its signature (pkgutil --check-signature, codesign --verify, spctl --assess, gpg --verify, rpm -K).";
    }

    // ---- reading the text ----

    /// <summary>
    /// Reads <paramref name="script"/> into statements, flattened, and adds them to <paramref name="everything"/>,
    /// and to <paramref name="checkable"/> when a check in them counts. The bodies of its here-documents, and the
    /// program a shell is given as a string (<c>sudo -u "$u" bash -c 'curl ... | sh'</c>), are read after it, each
    /// as a script of its own.
    /// </summary>
    private static void Read(string script, bool powerShell, bool checksCount, List<Stmt> everything, List<Stmt> checkable, StringBuilder code, int depth)
    {
        if (depth > MaxNesting)
        {
            throw new InvalidOperationException("Here-documents nested too deeply to read.");
        }

        var bodies = new List<(string Body, bool Code)>();
        var cleaned = RemoveComments(JoinContinuations(HereDocuments(script, powerShell, bodies), powerShell), powerShell);
        code.Append(cleaned).Append('\n');
        foreach (var statement in SplitStatements(cleaned, powerShell).Select(raw => Parse(raw, powerShell, 0)).SelectMany(Flatten))
        {
            everything.Add(statement);
            if (checksCount)
            {
                checkable.Add(statement);
            }

            bodies.AddRange(statement.Segments.Select(segment => ProgramGiven(segment, powerShell)).OfType<string>().Select(program => (program, true)));
        }

        foreach (var (body, isCode) in bodies)
        {
            Read(body, powerShell, checksCount && isCode, everything, checkable, code, depth + 1);
        }
    }

    /// <summary>The program a shell of the script's own language is handed as a string (<c>bash -c '...'</c>,
    /// <c>pwsh -Command '...'</c>), or null.</summary>
    private static string? ProgramGiven(List<Token> segment, bool powerShell)
    {
        var at = CommandIndex(segment);
        var command = at < 0 ? null : Basename(segment[at].Text);
        if (command is null || !(powerShell ? command is "pwsh" or "powershell" : command is "bash" or "sh" or "zsh" or "dash" or "ksh"))
        {
            return null;
        }

        for (var i = at + 1; i < segment.Count - 1; i++)
        {
            if (!segment[i].Quoted && Regex.IsMatch(segment[i].Text, "^-[A-Za-z]*c$|^-command$", Insensitive, MatchTimeout))
            {
                return segment[i + 1].Text;
            }
        }

        return null;
    }

    /// <summary>
    /// Takes the bodies of here-documents (<c>&lt;&lt;EOF</c>, <c>&lt;&lt;-EOF</c>, <c>&lt;&lt;'EOF'</c>,
    /// <c>&lt;&lt;"EOF"</c>) and PowerShell here-strings (<c>@' … '@</c>, <c>@" … "@</c>) out of the text before
    /// anything else tracks quotes: an apostrophe in one ("Couldn't update") would otherwise open a quote that
    /// swallows the code after it, an unchecked download included. Each body is read on its own instead, so its
    /// quotes end with it. A body fed to a shell (<c>bash &lt;&lt;EOF</c>, <c>cat &lt;&lt;EOF | sh</c>,
    /// <c>iex @' … '@</c>) is code like any other. Any other body is text: still read for downloads and runs (it
    /// may be a script written to disk and run later), but a check in it does not count, so a usage message
    /// that names <c>pkgutil --check-signature</c> verifies nothing. A here-document with no line that ends it is
    /// left where it is, as code: an arithmetic <c>1 &lt;&lt; 2</c> must not hide the rest of the script.
    /// </summary>
    private static string HereDocuments(string text, bool powerShell, List<(string Body, bool Code)> bodies)
    {
        var output = new StringBuilder(text.Length);
        var pending = new List<(string Delimiter, bool StripTabs)>();
        var lineStart = 0;
        char quote = '\0';
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quote != '\0')
            {
                output.Append(c);
                if (EscapesNext(c, quote, powerShell) && i + 1 < text.Length)
                {
                    output.Append(text[++i]);
                }
                else if (c == quote)
                {
                    quote = '\0';
                }

                continue;
            }

            if (c == '\n')
            {
                output.Append(c);
                var next = i + 1;
                if (pending.Count > 0)
                {
                    var isCode = FeedsAShell(text[lineStart..i], powerShell);
                    foreach (var (delimiter, stripTabs) in pending)
                    {
                        var end = Terminator(text, next, line => (stripTabs ? line.TrimStart('\t') : line) == delimiter, out var after);
                        if (end >= 0)
                        {
                            bodies.Add((text[next..end], isCode));
                            next = after;
                        }
                    }

                    pending.Clear();
                }

                i = next - 1;
                lineStart = next;
                continue;
            }

            if (c is '\'' or '"')
            {
                quote = c;
                output.Append(c);
                continue;
            }

            if (powerShell && c == '<' && i + 1 < text.Length && text[i + 1] == '#')
            {
                var end = text.IndexOf("#>", i + 2, StringComparison.Ordinal);
                end = end < 0 ? text.Length : end + 2;
                output.Append(text, i, end - i);
                i = end - 1;
                continue;
            }

            if (c == '#' && StartsAWord(text, i))
            {
                // A comment: its quotes and here-document markers mean nothing.
                var end = text.IndexOf('\n', i);
                end = end < 0 ? text.Length : end;
                output.Append(text, i, end - i);
                i = end - 1;
                continue;
            }

            if (c == (powerShell ? '`' : '\\') && i + 1 < text.Length)
            {
                output.Append(c).Append(text[++i]);
                continue;
            }

            if (powerShell && c == '@' && i + 1 < text.Length && text[i + 1] is '\'' or '"'
                && OnlyWhitespaceToEndOfLine(text, i + 2, out var bodyStart))
            {
                var close = text[i + 1];
                var end = Terminator(text, bodyStart, line => line.TrimStart().StartsWith($"{close}@", StringComparison.Ordinal), out _);
                if (end >= 0)
                {
                    var lineEnd = text.IndexOf('\n', i);
                    bodies.Add((text[bodyStart..end], FeedsAShell(text[lineStart..lineEnd], powerShell)));
                    // The here-string stays a (blank) string, so the statement around it still reads as one.
                    output.Append("''");
                    i = text.IndexOf($"{close}@", end, StringComparison.Ordinal) + 1;
                    continue;
                }
            }

            if (!powerShell && c == '<' && i + 1 < text.Length && text[i + 1] == '<')
            {
                if (i + 2 < text.Length && text[i + 2] == '<')
                {
                    // <<< is a here-string: one word, no body.
                    output.Append("<<<");
                    i += 2;
                    continue;
                }

                var j = i + 2;
                var stripTabs = j < text.Length && text[j] == '-';
                if (stripTabs)
                {
                    j++;
                }

                while (j < text.Length && text[j] is ' ' or '\t')
                {
                    j++;
                }

                var start = j;
                char inDelimiter = '\0';
                while (j < text.Length && (inDelimiter != '\0' || !(char.IsWhiteSpace(text[j]) || text[j] is ';' or '|' or '&' or '<' or '>' or '(' or ')')))
                {
                    if (inDelimiter == '\0' && text[j] is '\'' or '"')
                    {
                        inDelimiter = text[j];
                    }
                    else if (text[j] == inDelimiter)
                    {
                        inDelimiter = '\0';
                    }

                    j++;
                }

                var delimiter = text[start..j].Replace("'", "").Replace("\"", "").Replace("\\", "");
                if (delimiter.Length > 0)
                {
                    pending.Add((delimiter, stripTabs));
                }

                output.Append(text, i, j - i);
                i = j - 1;
                continue;
            }

            output.Append(c);
        }

        return output.ToString();
    }

    /// <summary>Where the first line from <paramref name="start"/> that <paramref name="ends"/> begins, or -1;
    /// <paramref name="after"/> is where the line after it begins.</summary>
    private static int Terminator(string text, int start, Func<string, bool> ends, out int after)
    {
        for (var at = start; at < text.Length;)
        {
            var newline = text.IndexOf('\n', at);
            var lineEnd = newline < 0 ? text.Length : newline;
            if (ends(text[at..lineEnd].TrimEnd('\r')))
            {
                after = newline < 0 ? text.Length : newline + 1;
                return at;
            }

            at = lineEnd + 1;
        }

        after = text.Length;
        return -1;
    }

    private static bool OnlyWhitespaceToEndOfLine(string text, int start, out int nextLine)
    {
        var newline = text.IndexOf('\n', start);
        nextLine = newline + 1;
        return newline >= 0 && string.IsNullOrWhiteSpace(text[start..newline]);
    }

    /// <summary>Whether the line that opens a here-document hands its body to a shell (or to code built from it).</summary>
    private static bool FeedsAShell(string line, bool powerShell) =>
        ScriptBlockFromText.IsMatch(line)
        || SplitStatements(line, powerShell).Select(raw => Parse(raw, powerShell, 0)).SelectMany(Flatten)
            .Any(s => s.Segments.Any(IsAnInterpreter));

    /// <summary>Lines ending in a backtick (PowerShell) or a backslash (bash) continue on the next line.</summary>
    private static string JoinContinuations(string script, bool powerShell)
    {
        var continuation = powerShell ? '`' : '\\';
        var output = new StringBuilder(script.Length);
        foreach (var raw in script.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var trimmed = line.TrimEnd();
            if (trimmed.EndsWith(continuation))
            {
                output.Append(trimmed.AsSpan(0, trimmed.Length - 1)).Append(' ');
            }
            else
            {
                output.Append(line).Append('\n');
            }
        }

        return output.ToString();
    }

    /// <summary>
    /// Removes PowerShell block comments and every comment that starts a word outside quotes, to the end
    /// of its line. A <c>#</c> inside a word (a URL fragment, <c>$#</c>, <c>${#array[@]}</c>) is not a comment.
    /// </summary>
    private static string RemoveComments(string code, bool powerShell)
    {
        var output = new StringBuilder(code.Length);
        char quote = '\0';
        for (var i = 0; i < code.Length; i++)
        {
            var c = code[i];
            if (quote != '\0')
            {
                output.Append(c);
                if (EscapesNext(c, quote, powerShell) && i + 1 < code.Length)
                {
                    output.Append(code[++i]);
                }
                else if (c == quote)
                {
                    quote = '\0';
                }

                continue;
            }

            if (c is '\'' or '"')
            {
                quote = c;
                output.Append(c);
                continue;
            }

            if (powerShell && c == '<' && i + 1 < code.Length && code[i + 1] == '#')
            {
                var end = code.IndexOf("#>", i + 2, StringComparison.Ordinal);
                i = end < 0 ? code.Length : end + 1;
                output.Append(' ');
                continue;
            }

            if (c == '#' && StartsAWord(code, i))
            {
                while (i < code.Length && code[i] != '\n')
                {
                    i++;
                }

                if (i < code.Length)
                {
                    output.Append('\n');
                }

                continue;
            }

            if (c == (powerShell ? '`' : '\\') && i + 1 < code.Length)
            {
                output.Append(c).Append(code[++i]);
                continue;
            }

            output.Append(c);
        }

        return output.ToString();
    }

    private static bool StartsAWord(string code, int index) =>
        index == 0 || char.IsWhiteSpace(code[index - 1]) || code[index - 1] is ';' or '(' or '|' or '&';

    /// <summary>Whether <paramref name="c"/>, inside <paramref name="quote"/>, escapes the character after it.
    /// Single quotes escape nothing in bash; in PowerShell a double-quoted string escapes with a backtick.</summary>
    private static bool EscapesNext(char c, char quote, bool powerShell) =>
        quote == '"' && c == (powerShell ? '`' : '\\');

    /// <summary>Cuts the script into statements at newlines, <c>;</c>, <c>&amp;&amp;</c> and <c>||</c> that are outside
    /// quotes, outside parentheses and outside a PowerShell hashtable (<c>@{ ... }</c>), so a splat written over
    /// several lines (<c>$p = @{ FilePath = $out ... }</c>, then <c>Start-Process @p</c>) is one assignment.</summary>
    private static IEnumerable<string> SplitStatements(string code, bool powerShell)
    {
        var current = new StringBuilder();
        char quote = '\0';
        var depth = 0;
        var braces = new Stack<bool>();
        var hashtables = 0;
        for (var i = 0; i < code.Length; i++)
        {
            var c = code[i];
            if (quote != '\0')
            {
                current.Append(c);
                if (EscapesNext(c, quote, powerShell) && i + 1 < code.Length)
                {
                    current.Append(code[++i]);
                }
                else if (c == quote)
                {
                    quote = '\0';
                }

                continue;
            }

            if (c is '\'' or '"')
            {
                quote = c;
                current.Append(c);
                continue;
            }

            if (c == (powerShell ? '`' : '\\') && i + 1 < code.Length)
            {
                current.Append(c).Append(code[++i]);
                continue;
            }

            if (c == '(')
            {
                depth++;
            }
            else if (c == ')' && depth > 0)
            {
                depth--;
            }
            else if (powerShell && c == '{')
            {
                var isHashtable = i > 0 && code[i - 1] == '@';
                braces.Push(isHashtable);
                hashtables += isHashtable ? 1 : 0;
            }
            else if (powerShell && c == '}' && braces.Count > 0 && braces.Pop())
            {
                hashtables--;
            }

            var isSeparator = depth == 0 && hashtables == 0 && (c is '\n' or ';'
                || (c == '&' && i + 1 < code.Length && code[i + 1] == '&')
                || (c == '|' && i + 1 < code.Length && code[i + 1] == '|'));
            if (isSeparator)
            {
                if (c is '&' or '|')
                {
                    i++;
                }

                if (current.ToString().Trim().Length > 0)
                {
                    yield return current.ToString();
                }

                current.Clear();
                continue;
            }

            current.Append(c);
        }

        if (current.ToString().Trim().Length > 0)
        {
            yield return current.ToString();
        }
    }

    private const int MaxNesting = 16;

    /// <summary>One statement into pipeline segments and words. A command substitution (<c>$(...)</c>, backticks,
    /// <c>&lt;(...)</c>) or a parenthesised group becomes nested statements, cut at <c>&amp;&amp;</c>, <c>;</c> and
    /// newlines like the script around it, so <c>(cd /tmp &amp;&amp; curl -O ...)</c> is seen to fetch. Nesting
    /// deeper than <see cref="MaxNesting"/> throws, which <see cref="Analyze"/> turns into Unverified: dropping
    /// what lies deeper would hide it.</summary>
    private static Stmt Parse(string raw, bool powerShell, int nesting)
    {
        var stmt = new Stmt { Raw = raw };
        var segment = stmt.Segments[0];
        var word = new StringBuilder();
        var wordQuoted = false;
        var hasWord = false;
        char quote = '\0';

        void Flush()
        {
            if (hasWord)
            {
                segment.Add(new Token(word.ToString(), wordQuoted));
            }

            word.Clear();
            wordQuoted = false;
            hasWord = false;
        }

        void Nest(string inner, bool subshell = false)
        {
            if (inner.Trim().Length == 0)
            {
                return;
            }

            if (nesting >= MaxNesting)
            {
                throw new InvalidOperationException("Nested too deeply to read.");
            }

            var parts = SplitStatements(inner, powerShell).ToList();
            foreach (var part in parts)
            {
                var nested = Parse(part, powerShell, nesting + 1);
                nested.Grouped = subshell && parts.Count > 1;
                stmt.Nested.Add(nested);
            }
        }

        for (var i = 0; i < raw.Length; i++)
        {
            var c = raw[i];

            if (quote == '\'')
            {
                if (c == '\'')
                {
                    quote = '\0';
                }
                else
                {
                    word.Append(c);
                }

                continue;
            }

            if (quote == '"')
            {
                if (c == '"')
                {
                    quote = '\0';
                }
                else if (EscapesNext(c, quote, powerShell) && i + 1 < raw.Length)
                {
                    word.Append(raw[++i]);
                }
                else if (c == '$' && i + 1 < raw.Length && raw[i + 1] == '(')
                {
                    var inner = CaptureBalanced(raw, i + 2, out var end);
                    word.Append(raw, i, end - i + 1 > raw.Length - i ? raw.Length - i : end - i + 1);
                    Nest(inner);
                    i = end;
                }
                else if (!powerShell && c == '`')
                {
                    var end = raw.IndexOf('`', i + 1);
                    end = end < 0 ? raw.Length - 1 : end;
                    Nest(raw.Substring(i + 1, Math.Max(0, end - i - 1)));
                    word.Append(raw, i, end - i + 1);
                    i = end;
                }
                else
                {
                    word.Append(c);
                }

                continue;
            }

            if (char.IsWhiteSpace(c))
            {
                Flush();
                continue;
            }

            switch (c)
            {
                case '\'':
                    quote = '\'';
                    wordQuoted = true;
                    hasWord = true;
                    continue;
                case '"':
                    quote = '"';
                    wordQuoted = true;
                    hasWord = true;
                    continue;
                case '|':
                    Flush();
                    segment = new List<Token>();
                    stmt.Segments.Add(segment);
                    continue;
                case '>':
                {
                    // A file descriptor in front ("2>") belongs to the redirect, not to a word.
                    if (hasWord && word.Length == 1 && char.IsDigit(word[0]) && !wordQuoted)
                    {
                        word.Clear();
                        hasWord = false;
                    }

                    Flush();
                    while (i + 1 < raw.Length && raw[i + 1] == '>')
                    {
                        i++;
                    }

                    if (i + 1 < raw.Length && raw[i + 1] == '&')
                    {
                        i++;
                        while (i + 1 < raw.Length && (char.IsDigit(raw[i + 1]) || raw[i + 1] == '-'))
                        {
                            i++;
                        }

                        continue;
                    }

                    var j = i + 1;
                    while (j < raw.Length && char.IsWhiteSpace(raw[j]))
                    {
                        j++;
                    }

                    var start = j;
                    char target = '\0';
                    while (j < raw.Length)
                    {
                        var d = raw[j];
                        if (target != '\0')
                        {
                            if (d == target)
                            {
                                target = '\0';
                            }
                        }
                        else if (d is '\'' or '"')
                        {
                            target = d;
                        }
                        else if (char.IsWhiteSpace(d) || d is '|' or ';')
                        {
                            break;
                        }

                        j++;
                    }

                    if (j > start)
                    {
                        stmt.Redirects.Add(raw.Substring(start, j - start).Trim('"', '\''));
                    }

                    i = j - 1;
                    continue;
                }
                case '`' when !powerShell:
                {
                    var end = raw.IndexOf('`', i + 1);
                    end = end < 0 ? raw.Length - 1 : end;
                    Nest(raw.Substring(i + 1, Math.Max(0, end - i - 1)));
                    word.Append(raw, i, end - i + 1);
                    hasWord = true;
                    i = end;
                    continue;
                }
                case '`' when i + 1 < raw.Length:
                    word.Append(raw[++i]);
                    hasWord = true;
                    continue;
                case '$' when i + 1 < raw.Length && raw[i + 1] == '(':
                case '<' when i + 1 < raw.Length && raw[i + 1] == '(':
                {
                    var inner = CaptureBalanced(raw, i + 2, out var end);
                    word.Append(raw, i, Math.Min(end - i + 1, raw.Length - i));
                    hasWord = true;
                    Nest(inner);
                    i = end;
                    continue;
                }
                case '(':
                {
                    var inner = CaptureBalanced(raw, i + 1, out var end);
                    var subshell = !hasWord && segment.Count == 0;
                    if (subshell)
                    {
                        Flush();
                    }
                    else
                    {
                        // An argument keeps its text, so -OutFile (Join-Path $env:TEMP 'app.msi') still says where
                        // the file went, and @(...) or args=(...) stays one word.
                        word.Append(raw, i, Math.Min(end - i + 1, raw.Length - i));
                        hasWord = true;
                    }

                    Nest(inner, subshell);
                    i = end;
                    continue;
                }
                case '&' when !(hasWord) && (i + 1 >= raw.Length || char.IsWhiteSpace(raw[i + 1])):
                    segment.Add(new Token("&", false));
                    continue;
                default:
                    word.Append(c);
                    hasWord = true;
                    continue;
            }
        }

        Flush();
        return stmt;
    }

    /// <summary>The text up to the parenthesis that closes the one opened just before <paramref name="start"/>,
    /// skipping quoted text. <paramref name="end"/> is the index of that closing parenthesis, or the last
    /// character when there is none.</summary>
    private static string CaptureBalanced(string text, int start, out int end)
    {
        var depth = 1;
        char quote = '\0';
        for (var i = start; i < text.Length; i++)
        {
            var c = text[i];
            if (quote != '\0')
            {
                if (c == quote)
                {
                    quote = '\0';
                }

                continue;
            }

            if (c is '\'' or '"')
            {
                quote = c;
            }
            else if (c == '(')
            {
                depth++;
            }
            else if (c == ')' && --depth == 0)
            {
                end = i;
                return text.Substring(start, i - start);
            }
        }

        end = text.Length - 1;
        return start <= text.Length ? text[Math.Min(start, text.Length)..] : string.Empty;
    }

    private static IEnumerable<Stmt> Flatten(Stmt statement)
    {
        yield return statement;
        foreach (var nested in statement.Nested)
        {
            foreach (var inner in Flatten(nested))
            {
                yield return inner;
            }
        }
    }

    // ---- what a segment is ----

    /// <summary>The command a segment runs, lower-cased and without its path or <c>.exe</c> (see <see cref="CommandIndex"/>).</summary>
    private static string? CommandWord(List<Token> segment)
    {
        var at = CommandIndex(segment);
        return at < 0 ? null : Basename(segment[at].Text);
    }

    /// <summary>Where the command a segment runs is: the first word that is not an environment assignment, a
    /// PowerShell assignment target, a shell keyword, an option, a parenthesised condition (<c>if (...)</c>),
    /// <c>sudo</c> or a <see cref="Wrappers">wrapper</see> and their options. -1 when there is none.</summary>
    private static int CommandIndex(List<Token> tokens)
    {
        var afterCall = false;
        for (var i = 0; i < tokens.Count; i++)
        {
            var text = tokens[i].Text;
            var called = afterCall;
            afterCall = false;
            if (!tokens[i].Quoted)
            {
                if (CommandPrefixes.Contains(text))
                {
                    if (text.Equals("sudo", StringComparison.OrdinalIgnoreCase))
                    {
                        while (i + 1 < tokens.Count && tokens[i + 1].Text.StartsWith('-') && !tokens[i + 1].Quoted)
                        {
                            i += SudoOptionsWithValue.Contains(tokens[i + 1].Text) ? 2 : 1;
                        }
                    }

                    afterCall = text == "&";
                    continue;
                }

                if (Wrappers.Contains(Basename(text)))
                {
                    while (i + 1 < tokens.Count && !tokens[i + 1].Quoted && WrapperArgument.IsMatch(tokens[i + 1].Text))
                    {
                        i++;
                    }

                    continue;
                }

                // `& (Join-Path $dir 'setup.exe')` calls what the group names; anywhere else a group in command
                // position is a condition or an expression, not a command.
                if (text.StartsWith('-') || ShellVariableAssignment.IsMatch(text) || (!called && text.StartsWith('(')))
                {
                    continue;
                }
            }

            if (text.StartsWith('$') && i + 1 < tokens.Count && tokens[i + 1].Text is "=" or "+=" && !tokens[i + 1].Quoted)
            {
                i++;
                continue;
            }

            return BehindAWrapper(tokens, i);
        }

        return -1;
    }

    /// <summary>
    /// An unrecognised command directly in front of a known one is taken for a wrapper (<c>retry 3 curl ...</c>,
    /// <c>$SUDO installer ...</c>): the known one is what runs. Only numbers and variables may sit between them,
    /// so neither a sentence ("do not run curl ...") nor an option's value is read as a command. Tests and prints
    /// are never wrappers: <c>[ -x /usr/sbin/installer ]</c> and <c>echo curl</c> run neither.
    /// </summary>
    private static int BehindAWrapper(List<Token> tokens, int at)
    {
        var command = Basename(tokens[at].Text);
        if (IsKnown(command) || PrintCommands.Contains(command) || command is "[" or "[[" or "test")
        {
            return at;
        }

        for (var j = at + 1; j < tokens.Count; j++)
        {
            if (!tokens[j].Quoted && IsKnown(Basename(tokens[j].Text)))
            {
                return j;
            }

            if (!UnknownWrapperArgument.IsMatch(tokens[j].Text))
            {
                break;
            }
        }

        return at;
    }

    /// <summary>A command this check knows to fetch or run. Not ".", which as a word is far more often a directory
    /// (find . -name ...) than dot-sourcing.</summary>
    private static bool IsKnown(string command) =>
        command != "." && (FetchCommands.Contains(command) || RunCommands.Contains(command) || Interpreters.Contains(command) || command == "certutil");

    private static string Basename(string command)
    {
        var name = command;
        var slash = Math.Max(name.LastIndexOf('/'), name.LastIndexOf('\\'));
        if (slash >= 0 && slash < name.Length - 1)
        {
            name = name[(slash + 1)..];
        }

        name = name.ToLowerInvariant();
        return name.EndsWith(".exe", StringComparison.Ordinal) ? name[..^4] : name;
    }

    private static bool SegmentFetches(List<Token> segment)
    {
        var command = CommandWord(segment);
        if (command is null)
        {
            return false;
        }

        if (FetchCommands.Contains(command))
        {
            return true;
        }

        return command == "certutil" && segment.Any(t => t.Text.Equals("-urlcache", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsAnInterpreter(List<Token> segment)
    {
        var command = CommandWord(segment);
        return command is not null && Interpreters.Contains(command);
    }

    private static bool FetchesAnywhere(Stmt statement) =>
        Flatten(statement).Any(s => s.Segments.Any(SegmentFetches) || DownloadStringCall.IsMatch(s.Raw));

    // ---- a download that is run without being saved ----

    private static bool PipesIntoAnInterpreter(Stmt statement)
    {
        for (var i = 1; i < statement.Segments.Count; i++)
        {
            if (IsAnInterpreter(statement.Segments[i])
                && statement.Segments.Take(i).Any(SegmentFetches))
            {
                return true;
            }
        }

        // bash -c "$(curl ...)", eval "$(curl ...)", sh <(curl ...), iex (irm ...), iex ((New-Object ...).DownloadString(...)).
        if (statement.Segments.Count > 0 && IsAnInterpreter(statement.Segments[0]))
        {
            if (statement.Nested.Any(FetchesAnywhere) || DownloadStringCall.IsMatch(statement.Raw))
            {
                return true;
            }
        }

        // & ([scriptblock]::Create((irm ...))): the command word is the call operator, but what it calls is built
        // from a download.
        return ScriptBlockFromText.IsMatch(statement.Raw)
            && (statement.Nested.Any(FetchesAnywhere) || DownloadStringCall.IsMatch(statement.Raw));
    }

    // ---- a download that is saved ----

    private static List<Fetch> FindFetches(List<Stmt> statements, string code, bool powerShell)
    {
        var fetches = new List<Fetch>();
        foreach (var statement in statements)
        {
            var wroteAFile = false;
            for (var i = 0; i < statement.Segments.Count; i++)
            {
                var segment = statement.Segments[i];
                if (!SegmentFetches(segment))
                {
                    continue;
                }

                var (hasFile, target, url) = WhereItWrites(segment, code);
                if (!hasFile)
                {
                    // To a redirect (curl url > file), or into a command that unpacks it as it arrives.
                    if (statement.Segments.Skip(i + 1).Any(Unpacks))
                    {
                        fetches.Add(new Fetch(statement, null, url ?? FirstUrl(segment), true));
                        wroteAFile = true;
                        continue;
                    }

                    var redirect = statement.Redirects.FirstOrDefault(r => !IsDiscard(r));
                    var tee = statement.Segments.Skip(i + 1).Select(TeeTarget).FirstOrDefault(t => t is not null);
                    var file = redirect ?? tee;
                    if (file is not null)
                    {
                        fetches.Add(new Fetch(statement, Clean(file), url ?? FirstUrl(segment), false));
                        wroteAFile = true;
                    }

                    continue;
                }

                fetches.Add(new Fetch(statement, target, url, false));
                wroteAFile = true;
            }

            if (!wroteAFile)
            {
                var call = DownloadFileCall.Match(statement.Raw);
                if (call.Success)
                {
                    fetches.Add(new Fetch(statement, Clean(call.Groups["target"].Value), Clean(call.Groups["url"].Value), false));
                }
            }

            // script=$(curl ...) or $s = irm ...: the body kept in a variable, which only matters if that
            // variable is later run as code (eval "$script", iex $s).
            if (AssignedVariable(statement, powerShell) is { } variable && FetchesAnywhere(statement))
            {
                fetches.Add(new Fetch(statement, "$" + variable, null, false, Content: true));
            }
        }

        return fetches;
    }

    /// <summary>Whether a fetching segment writes the body to a file of its own accord, and which.</summary>
    private static (bool HasFile, string? Target, string? Url) WhereItWrites(List<Token> segment, string code)
    {
        // From the command on: a wrapper's own options (timeout -s KILL 300 curl ...) are not curl's.
        var at = CommandIndex(segment);
        var command = Basename(segment[at].Text);
        var args = segment.Skip(at).Where(t => !t.Text.Equals(string.Empty)).ToList();
        var url = FirstUrl(segment);

        string? NextAfter(Func<string, bool> flag)
        {
            for (var i = 0; i < args.Count - 1; i++)
            {
                if (!args[i].Quoted && flag(args[i].Text))
                {
                    return args[i + 1].Text;
                }
            }

            return null;
        }

        switch (command)
        {
            case "curl":
            {
                var output = NextAfter(t => t == "-o" || t == "--output" || (IsShortCluster(t) && t.EndsWith('o')));
                output ??= args.Select(t => t.Text).Where(t => t.StartsWith("--output=", StringComparison.Ordinal)).Select(t => t["--output=".Length..]).FirstOrDefault();
                output ??= args.Where(t => !t.Quoted).Select(t => Attached(t.Text, 'o', CurlOptionsWithValue)).FirstOrDefault(v => v is not null);
                if (output is not null)
                {
                    return IsDiscard(output) ? (false, null, url) : (true, Clean(output), url);
                }

                var remoteName = args.Any(t => !t.Quoted
                    && (t.Text is "--remote-name" or "--remote-name-all" || (IsShortCluster(t.Text) && t.Text.Contains('O'))));
                return remoteName ? (true, FileNameOf(url), url) : (false, null, url);
            }

            case "wget":
            {
                var stdout = args.Any(t => !t.Quoted
                    && (t.Text.StartsWith("-", StringComparison.Ordinal) && t.Text.TrimStart('-').EndsWith("O-", StringComparison.Ordinal)));
                var output = NextAfter(t => t == "-O" || t == "--output-document" || (IsShortCluster(t) && t.EndsWith('O')));
                output ??= args.Select(t => t.Text).Where(t => t.StartsWith("--output-document=", StringComparison.Ordinal)).Select(t => t["--output-document=".Length..]).FirstOrDefault();
                output ??= args.Where(t => !t.Quoted).Select(t => Attached(t.Text, 'O', WgetOptionsWithValue)).FirstOrDefault(v => v is not null);
                if (stdout || (output is not null && IsDiscard(output)))
                {
                    return (false, null, url);
                }

                return (true, output is not null ? Clean(output) : FileNameOf(url), url);
            }

            case "invoke-webrequest" or "iwr" or "invoke-restmethod" or "irm":
            {
                var outFile = NextAfter(t => Regex.IsMatch(t, @"^-Out(?:F(?:i(?:l(?:e)?)?)?)?$", Insensitive, MatchTimeout));
                if (outFile is null && args.Any(t => t.Text.StartsWith('@') && !t.Quoted))
                {
                    var splat = SplatTarget.Match(code);
                    outFile = splat.Success ? splat.Groups["target"].Value : null;
                }

                url ??= NextAfter(t => t.Equals("-Uri", StringComparison.OrdinalIgnoreCase));
                return outFile is null || IsDiscard(outFile) ? (false, null, url) : (true, Clean(outFile), url);
            }

            case "start-bitstransfer":
            {
                var destination = NextAfter(t => Regex.IsMatch(t, @"^-Dest(?:i(?:n(?:a(?:t(?:i(?:o(?:n)?)?)?)?)?)?)?$", Insensitive, MatchTimeout));
                if (destination is null && args.Any(t => t.Text.StartsWith('@') && !t.Quoted))
                {
                    var splat = SplatTarget.Match(code);
                    destination = splat.Success ? splat.Groups["target"].Value : null;
                }

                url ??= NextAfter(t => t.Equals("-Source", StringComparison.OrdinalIgnoreCase));
                // BITS always downloads to a file, even when the destination is left to the default.
                return (true, destination is null ? FileNameOf(url) : Clean(destination), url);
            }

            case "certutil":
            {
                var f = args.FindIndex(t => t.Text.Equals("-f", StringComparison.OrdinalIgnoreCase));
                var target = f >= 0 && f + 2 < args.Count ? args[f + 2].Text : null;
                return (true, target is null ? FileNameOf(url) : Clean(target), url);
            }

            default:
                return (false, null, url);
        }
    }

    /// <summary>curl's and wget's short options that take a value: in a cluster the first of them takes the rest
    /// of the word as its value.</summary>
    private const string CurlOptionsWithValue = "AbcCdDeEFHKmoPQrtTuUwxXyYz";

    private const string WgetOptionsWithValue = "aABDeiIloOPQRtTUwX";

    /// <summary>The value attached to <paramref name="option"/> in a short-option word (<c>-o/tmp/app.pkg</c>,
    /// <c>-fsSLo/tmp/app.pkg</c>, <c>-qO-</c>), or null when the word attaches none to it.</summary>
    private static string? Attached(string word, char option, string optionsWithValue)
    {
        if (word.Length < 3 || word[0] != '-' || word[1] == '-')
        {
            return null;
        }

        for (var i = 1; i < word.Length; i++)
        {
            if (optionsWithValue.Contains(word[i]))
            {
                return word[i] == option && i + 1 < word.Length ? word[(i + 1)..] : null;
            }

            if (!char.IsLetter(word[i]))
            {
                return null;
            }
        }

        return null;
    }

    /// <summary>A cluster of short options such as <c>-fsSLo</c> (not <c>--long</c>, not a lone <c>-</c>).</summary>
    private static bool IsShortCluster(string text) => Regex.IsMatch(text, @"^-[A-Za-z]+$", RegexOptions.CultureInvariant, MatchTimeout);

    private static string? FirstUrl(List<Token> segment) =>
        segment.Select(t => t.Text).FirstOrDefault(t => t.Contains("://", StringComparison.Ordinal));

    private static bool Unpacks(List<Token> segment)
    {
        var command = CommandWord(segment);
        return command switch
        {
            "tar" or "bsdtar" => segment.Any(t => !t.Quoted && (t.Text == "--extract"
                || Regex.IsMatch(t.Text, @"^-?[A-Za-z]*x[A-Za-z]*$", RegexOptions.CultureInvariant, MatchTimeout))),
            "unzip" or "7z" or "7za" => true,
            _ => false
        };
    }

    private static string? TeeTarget(List<Token> segment) =>
        CommandWord(segment) == "tee" ? segment.Skip(1).FirstOrDefault(t => !t.Text.StartsWith('-') && Basename(t.Text) != "tee")?.Text : null;

    /// <summary>Installed or run: an installer type in the file's name or its URL's, or the saved file named
    /// again by a statement that runs it. A fetch that is neither (a data file, an archive that is only
    /// stored) is not flagged.</summary>
    /// <remarks>"Named again" follows variables: one assigned from something that names the file
    /// (<c>$msiArgs = @('/i', $msiPath, '/qn')</c>, <c>args=(-pkg "$PKG")</c>) names it too, so
    /// <c>Start-Process msiexec -ArgumentList $msiArgs</c> runs it. Three of the real approved scripts install
    /// through exactly that array.</remarks>
    private static bool IsRunOrInstalled(Fetch fetch, List<Stmt> statements, List<(Stmt Statement, string Variable)> assignments, bool powerShell)
    {
        if (fetch.UnpackedAsItArrives)
        {
            return true;
        }

        if ((fetch.Target is not null && InstallerType.IsMatch(fetch.Target + " "))
            || (FileNameOf(fetch.Url) is { } fromUrl && InstallerType.IsMatch(fromUrl + " ")))
        {
            return true;
        }

        if (string.IsNullOrEmpty(fetch.Target))
        {
            // Nothing says where it went, so any command that runs something might be running it.
            return statements.Any(s => !ReferenceEquals(s, fetch.Statement) && RunsSomething(s, powerShell, null));
        }

        var names = Aliases(fetch.Target, assignments, powerShell);
        return statements.Any(s => !ReferenceEquals(s, fetch.Statement) && names.Any(n => n.IsMatch(s.Raw))
            && (fetch.Content ? RunsAsCode(s, names) : RunsSomething(s, powerShell, names)));
    }

    /// <summary>Whether the statement runs the text in one of <paramref name="names"/> as code: handed to an
    /// interpreter (<c>eval "$x"</c>, <c>bash -c "$x"</c>, <c>iex $x</c>), built into a script block, or piped into
    /// an interpreter that reads its program from the pipe (<c>echo "$x" | bash</c>) rather than one given its own
    /// (<c>printf '%s' "$json" | python3 -c '...'</c> only reads the text).</summary>
    private static bool RunsAsCode(Stmt statement, List<Regex> names) => Flatten(statement).Any(s =>
    {
        bool Named(IEnumerable<Token> tokens) => tokens.Any(t => names.Any(n => n.IsMatch(t.Text)));

        if (ScriptBlockFromText.IsMatch(s.Raw) && names.Any(n => n.IsMatch(s.Raw)))
        {
            return true;
        }

        for (var i = 0; i < s.Segments.Count; i++)
        {
            if (IsAnInterpreter(s.Segments[i])
                && (Named(s.Segments[i]) || (!HasItsOwnProgram(s.Segments[i]) && s.Segments.Take(i).Any(Named))))
            {
                return true;
            }
        }

        return false;
    });

    /// <summary><c>bash -c '...'</c>, <c>python3 -c '...'</c>, <c>perl -e '...'</c>, <c>pwsh -Command '...'</c>: the
    /// program is the argument, so what is piped in is data.</summary>
    private static bool HasItsOwnProgram(List<Token> segment)
    {
        var command = CommandWord(segment);
        return segment.Any(t => !t.Quoted && (Regex.IsMatch(t.Text, "^-[A-Za-z]*c$|^-command$", Insensitive, MatchTimeout)
            || (t.Text == "-e" && command is "perl" or "ruby" or "node")));
    }

    /// <summary>Patterns for every way a later statement can name the saved file: the file itself, and each
    /// variable assigned (directly or through another such variable) from text that names it.</summary>
    private static List<Regex> Aliases(string target, List<(Stmt Statement, string Variable)> assignments, bool powerShell)
    {
        var names = new List<Regex> { NamePattern(ReferenceToken(target), powerShell) };
        var followed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool grew;
        do
        {
            grew = false;
            foreach (var (statement, variable) in assignments)
            {
                if (!followed.Contains(variable) && names.Any(n => n.IsMatch(statement.Raw)))
                {
                    followed.Add(variable);
                    names.Add(new Regex(VariablePattern(variable, powerShell), Insensitive, MatchTimeout));
                    grew = true;
                }
            }
        }
        while (grew);

        return names;
    }

    /// <summary>What another line has to contain to be talking about the saved file: its file name when it has
    /// an extension, otherwise the whole path or variable.</summary>
    private static string ReferenceToken(string target)
    {
        if (target.StartsWith('('))
        {
            // A path built in place, (Join-Path $env:TEMP 'app.msi'): named by its last part.
            target = target.Trim('(', ')').Split(' ', '\t', ',').LastOrDefault(part => part.Length > 0)?.Trim('"', '\'') ?? target;
        }

        var name = target.Replace('\\', '/');
        name = name[(name.LastIndexOf('/') + 1)..];
        return name.Contains('.') && !name.StartsWith('$') ? name : target;
    }

    /// <summary>The saved file's name as a whole word, with each variable in it matched however it is spelled.
    /// Case is ignored in both languages: here a looser match only makes more statements count as running the
    /// file, which fails closed.</summary>
    private static Regex NamePattern(string token, bool powerShell)
    {
        var pattern = new StringBuilder(@"(?<![\w.\-])");
        var at = 0;
        foreach (Match variable in VariableReference.Matches(token))
        {
            pattern.Append(Regex.Escape(token[at..variable.Index])).Append(VariablePattern(variable.Groups["name"].Value, powerShell));
            at = variable.Index + variable.Length;
        }

        pattern.Append(Regex.Escape(token[at..])).Append(@"(?![\w.\-])");
        return new Regex(pattern.ToString(), Insensitive, MatchTimeout);
    }

    /// <summary><c>$name</c> or <c>${name}</c> (<c>${name[@]}</c> too), and in PowerShell the splat <c>@name</c>.</summary>
    private static string VariablePattern(string name, bool powerShell) =>
        (powerShell ? "[$@]" : @"\$") + @"(?:\{" + Regex.Escape(name) + @"(?!\w)[^}]*\}|" + Regex.Escape(name) + @"(?!\w))";

    private static readonly HashSet<string> RunCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "msiexec", "start-process", "installer", "hdiutil", "dpkg", "rpm", "apt", "apt-get", "dnf", "yum", "zypper",
        "chmod", "expand-archive", "unzip", "add-appxpackage", "add-appxprovisionedpackage", "bash", "sh", "zsh",
        "dash", "source", ".", "python", "python3", "perl", "ruby", "open", "invoke-item", "tar", "xattr", "install"
    };

    /// <summary>Whether the statement runs something. With <paramref name="names"/> (the saved file's), a
    /// command word that is one of them runs it by its path; with none, any path might be the file.</summary>
    private static bool RunsSomething(Stmt statement, bool powerShell, List<Regex>? names) =>
        Flatten(statement).Any(s => s.Segments.Any(segment => SegmentRuns(segment, powerShell, names)));

    private static bool SegmentRuns(List<Token> segment, bool powerShell, List<Regex>? names)
    {
        var at = CommandIndex(segment);
        if (at < 0)
        {
            return false;
        }

        var text = segment[at].Text;
        var command = Basename(text);
        if (RunCommands.Contains(command))
        {
            var arguments = segment.Skip(at + 1).ToList();
            return command switch
            {
                // These only run something when they install, make executable or unpack: not when they
                // list, query or remove.
                "chmod" => arguments.Any(t => GrantsExecute(t.Text)),
                "hdiutil" => arguments.Any(t => t.Text.Equals("attach", StringComparison.OrdinalIgnoreCase)),
                "dpkg" => arguments.Any(t => t.Text is "-i" or "--install"),
                "rpm" => arguments.Any(t => Regex.IsMatch(t.Text, @"^-[A-Za-z]*[iU][A-Za-z]*$|^--install$|^--upgrade$", RegexOptions.CultureInvariant, MatchTimeout)),
                "apt" or "apt-get" or "dnf" or "yum" or "zypper" => arguments.Any(t => t.Text.Equals("install", StringComparison.OrdinalIgnoreCase)),
                "tar" => Unpacks(segment),
                "xattr" => false,
                _ => true
            };
        }

        // Through PowerShell's call operator (& $out, & (Join-Path ...)), or a command held in a bash variable
        // ($SUDO ..., "$INSTALLER"): what that runs cannot be told, so it may be the file. A PowerShell
        // statement that starts with a bare variable only outputs its value.
        if (powerShell ? at > 0 && !segment[at - 1].Quoted && segment[at - 1].Text == "&" : text.StartsWith('$'))
        {
            return true;
        }

        if (powerShell && text.StartsWith('$'))
        {
            return false;
        }

        // The file run by its own path: /tmp/agent install, sudo ./install.sh, .\setup.exe. The test is on the
        // word as written: Basename has already stripped the directory from `command`.
        return names?.Any(n => n.IsMatch(text))
            ?? (text.Contains('/') || text.Contains('\\') || text.StartsWith('~'));
    }

    /// <summary>A chmod mode that lets someone execute the file: octal with an odd digit among the last three
    /// (755, 0700, 4755), or symbolic with x (+x, a+rx, u=rwx,go=rx). <c>--reference</c> copies a mode it cannot
    /// see, so it counts too.</summary>
    private static bool GrantsExecute(string mode) =>
        Regex.IsMatch(mode, "^[0-7]{1,4}$", RegexOptions.CultureInvariant, MatchTimeout)
            ? mode[^Math.Min(3, mode.Length)..].Any(digit => (digit - '0') % 2 == 1)
            : Regex.IsMatch(mode, @"(?:^|,)[ugoa]*[+=][rwxXst]*[xX]", RegexOptions.CultureInvariant, MatchTimeout)
              || mode.StartsWith("--reference", StringComparison.Ordinal);

    private static string? FileNameOf(string? url)
    {
        if (string.IsNullOrEmpty(url))
        {
            return null;
        }

        // By hand, not through Uri: the host is often a variable ("https://$HOST/app.dmg"), which Uri refuses.
        var end = url.IndexOfAny(new[] { '?', '#' });
        var path = (end < 0 ? url : url[..end]).Trim('"', '\'');
        var name = path[(path.LastIndexOf('/') + 1)..];
        return name.Length == 0 || name.Contains("://", StringComparison.Ordinal) ? null : name;
    }

    private static string Clean(string value) => value.Trim().Trim('"', '\'');

    /// <summary>/dev/null, $null, NUL and "-" are where output goes when nobody wants the file.</summary>
    private static bool IsDiscard(string target) => Clean(target) is "-" or "/dev/null" or "NUL" or "nul" or "$null";

    private static string DescribeSource(Stmt statement, string? target, string? url = null)
    {
        var from = url ?? statement.Segments.SelectMany(s => s).Select(t => t.Text)
            .FirstOrDefault(t => t.Contains("://", StringComparison.Ordinal))
            ?? statement.Nested.SelectMany(Flatten).SelectMany(s => s.Segments).SelectMany(s => s).Select(t => t.Text)
                .FirstOrDefault(t => t.Contains("://", StringComparison.Ordinal));
        if (from is not null)
        {
            // Just the URL, not the word around it (script=$(curl -fsSL https://...)).
            var just = Regex.Match(from, @"[A-Za-z][\w+.\-]*://[^\s""'()`]+", RegexOptions.CultureInvariant, MatchTimeout);
            return (just.Success ? just.Value : from).Trim('"', '\'', ')', '(', ',', '.', '`');
        }

        return string.IsNullOrEmpty(target) ? "a file" : target;
    }

    // ---- what counts as checking the download ----

    private static bool HasACheck(List<Stmt> statements, bool powerShell)
    {
        // PowerShell variables are case-insensitive ($Hash and $hash are one variable); bash's are not.
        var names = powerShell ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var hashVariables = new HashSet<string>(names);
        var signatureVariables = new HashSet<string>(names);
        foreach (var statement in statements)
        {
            var effective = Effective(statement);
            if (effective.Count == 0)
            {
                continue;
            }

            var compares = Compares(effective, powerShell);
            var hashes = effective.Any(IsAHashTool);
            var signs = effective.Any(s => s.Segments.Any(IsAuthenticode));

            if (effective.Any(s => s.Segments.Any(IsAnExplicitSignatureCheck)))
            {
                return true;
            }

            if (hashes && (compares || effective.Any(s => s.Segments.Any(HashesWithCheckFlag))))
            {
                return true;
            }

            if (signs && compares && effective.Any(s => ValidWord.IsMatch(s.Raw)))
            {
                return true;
            }

            var assigned = AssignedVariable(statement, powerShell);
            if (assigned is not null)
            {
                if (hashes && !compares)
                {
                    hashVariables.Add(assigned);
                }

                if (signs && !compares)
                {
                    signatureVariables.Add(assigned);
                }
            }

            if (compares)
            {
                if (hashVariables.Any(v => Refers(statement.Raw, v, powerShell)))
                {
                    return true;
                }

                if (signatureVariables.Any(v => Refers(statement.Raw, v, powerShell)) && ValidWord.IsMatch(statement.Raw))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>The statement and what it contains, minus anything that only prints: a string handed to echo,
    /// Write-Output and the like says what it likes without doing it. A print piped into something else
    /// (<c>echo "$sum  $file" | shasum -c -</c>) keeps what comes after the pipe. The statements of a subshell
    /// group are not part of it: each is read on its own.</summary>
    private static List<Stmt> Effective(Stmt statement)
    {
        var first = statement.Segments[0];
        var prints = IsPrint(first);
        if (!prints)
        {
            return Within(statement).ToList();
        }

        if (statement.Segments.Count == 1)
        {
            return new List<Stmt>();
        }

        var rest = new Stmt { Raw = statement.Raw };
        rest.Segments.Clear();
        rest.Segments.AddRange(statement.Segments.Skip(1));
        return new List<Stmt> { rest };
    }

    private static IEnumerable<Stmt> Within(Stmt statement) =>
        statement.Nested.Where(nested => !nested.Grouped).SelectMany(Within).Prepend(statement);

    private static bool IsPrint(List<Token> segment)
    {
        var command = CommandWord(segment);
        return command is not null && (PrintCommands.Contains(command) || command.StartsWith("[console]::", StringComparison.Ordinal));
    }

    private static bool IsAHashTool(Stmt statement) => statement.Segments.Any(segment =>
    {
        var command = CommandWord(segment);
        return command switch
        {
            "shasum" or "sha256sum" or "sha512sum" or "get-filehash" => true,
            "openssl" => segment.Any(t => t.Text is "dgst" or "sha256" or "sha512"),
            "certutil" => segment.Any(t => t.Text.Equals("-hashfile", StringComparison.OrdinalIgnoreCase)),
            _ => false
        };
    });

    /// <summary>shasum / sha256sum with -c or --check: the tool itself compares against a checksum list.</summary>
    private static bool HashesWithCheckFlag(List<Token> segment)
    {
        var command = CommandWord(segment);
        return command is "shasum" or "sha256sum" or "sha512sum"
            && segment.Any(t => !t.Quoted && (t.Text == "--check" || Regex.IsMatch(t.Text, @"^-[A-Za-z0-9]*c[A-Za-z0-9]*$", RegexOptions.CultureInvariant, MatchTimeout)));
    }

    private static bool IsAuthenticode(List<Token> segment) => CommandWord(segment) == "get-authenticodesignature";

    private static bool IsAnExplicitSignatureCheck(List<Token> segment)
    {
        var command = CommandWord(segment);
        bool Has(Func<string, bool> predicate) => segment.Any(t => !t.Quoted && predicate(t.Text));

        return command switch
        {
            "pkgutil" => Has(t => t == "--check-signature"),
            // -v / -vv / --verify check; -d and -dv only display what the signature says.
            "codesign" => Has(t => t == "--verify" || Regex.IsMatch(t, @"^-v+$", RegexOptions.CultureInvariant, MatchTimeout)),
            "spctl" => Has(t => t is "--assess" or "-a"),
            "gpg" or "gpg2" => Has(t => t == "--verify"),
            "gpgv" => true,
            "rpm" => Has(t => t == "--checksig" || Regex.IsMatch(t, @"^-[A-Za-z]*K[A-Za-z]*$", RegexOptions.CultureInvariant, MatchTimeout)),
            _ => false
        };
    }

    /// <summary>Whether any of the statements compares: a comparison operator, or a command that compares.
    /// PowerShell's <c>=</c> is an assignment and never a comparison.</summary>
    private static bool Compares(List<Stmt> statements, bool powerShell) => statements.Any(s => s.Segments.Any(segment =>
    {
        var command = CommandWord(segment);
        if (!powerShell && command is not null && BashCompareCommands.Contains(command))
        {
            return true;
        }

        return segment.Any(t => !t.Quoted && (powerShell ? PowerShellCompareOperators.Contains(t.Text) : BashCompareOperators.Contains(t.Text) || PowerShellCompareOperators.Contains(t.Text)));
    }));

    private static string? AssignedVariable(Stmt statement, bool powerShell)
    {
        var first = statement.Segments[0];
        if (first.Count == 0)
        {
            return null;
        }

        if (powerShell)
        {
            return first[0].Text.StartsWith('$') && first.Count > 1 && first[1].Text is "=" or "+=" ? first[0].Text.TrimStart('$') : null;
        }

        // local / export / declare -r NAME=...: still an assignment.
        var word = first.SkipWhile(t => !t.Quoted && (t.Text is "local" or "export" or "readonly" or "declare" or "typeset" || t.Text.StartsWith('-')))
            .FirstOrDefault();
        var match = Regex.Match(word?.Text ?? string.Empty, @"^(?<name>[A-Za-z_]\w*)(?:\[[^\]]*\])?\+?=", RegexOptions.CultureInvariant, MatchTimeout);
        return match.Success ? match.Groups["name"].Value : null;
    }

    private static bool Refers(string text, string variable, bool powerShell) =>
        Regex.IsMatch(text, @"\$\{?" + Regex.Escape(variable) + @"(?![\w])",
            powerShell ? Insensitive : RegexOptions.CultureInvariant, MatchTimeout);
}
