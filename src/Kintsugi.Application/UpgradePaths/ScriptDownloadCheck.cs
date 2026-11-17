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
/// pattern over the whole text: comments are removed (a trailing <c>#</c> too), the script is cut into
/// statements at newlines, <c>;</c>, <c>&amp;&amp;</c> and <c>||</c> outside quotes, each statement into
/// pipeline segments and words, and a command substitution or parenthesised group is read as a statement of
/// its own. What a statement does is decided from its command words, never from a word that happens to
/// appear somewhere (in a string it prints, in a comment, as the extension of <c>curl.exe</c>).
/// </para>
/// <para>
/// It flags a file fetched from the network (<c>curl</c> to a file or a redirect, <c>wget</c>,
/// <c>Invoke-WebRequest</c> straight or splatted, BITS, <c>DownloadFile</c>, <c>certutil -urlcache</c>) and then
/// installed or run (an installer type, or the saved file named again by a command that runs something, in
/// the same line or a later one), a download unpacked as it streams in (<c>curl | tar -x</c>), and a download
/// run without ever being saved (<c>curl | bash</c>, <c>bash -c "$(curl ...)"</c>, <c>iex (irm ...)</c>), when
/// nothing in the script checks it.
/// </para>
/// <para>
/// What counts as a check, in a statement that does it rather than prints it: a hash tool
/// (<c>Get-FileHash</c>, <c>shasum</c>, <c>sha256sum</c>, <c>openssl dgst</c>, <c>certutil -hashfile</c>) used with
/// <c>-c</c> or in the same statement as a comparison, or whose result is stored in a variable that a later
/// statement compares; <c>Get-AuthenticodeSignature</c> tested for <c>Valid</c> the same way;
/// <c>pkgutil --check-signature</c>, <c>codesign --verify</c> (not <c>-d</c>, which only displays),
/// <c>spctl --assess</c>, <c>gpg --verify</c>, <c>rpm -K</c>. A hash that is only printed is not a check.
/// </para>
/// <para>
/// **It fails closed.** A script it cannot read (the analysis throws or times out) is
/// <see cref="DownloadVerification.Unverified"/>, never <see cref="DownloadVerification.NoDownload"/>: a
/// timeout must not be a way to get a script signed.
/// </para>
/// <para>
/// Blind spots remain. It does not follow variables or function calls, so a fetch through a helper
/// function goes unseen. It cannot tell that a check runs BEFORE the installer, that it is applied to the
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

    private static readonly HashSet<string> BashCompareCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "diff", "cmp", "grep", "test", "[", "[["
    };

    // ---- what the script is made of ----

    private sealed record Token(string Text, bool Quoted);

    private sealed class Stmt
    {
        public string Raw = string.Empty;
        public readonly List<List<Token>> Segments = new() { new List<Token>() };
        public readonly List<Stmt> Nested = new();
        public readonly List<string> Redirects = new();
    }

    private sealed record Fetch(Stmt Statement, string? Target, string? Url, bool UnpackedAsItArrives);

    private static ScriptDownloadAssessment Assess(string script, ScriptLanguage language, string? platform)
    {
        var powerShell = language == ScriptLanguage.PowerShell;
        var code = RemoveComments(JoinContinuations(script, powerShell), powerShell);
        var statements = SplitStatements(code, powerShell).Select(raw => Parse(raw, powerShell, 0)).ToList();
        var everything = statements.SelectMany(Flatten).ToList();

        var piped = everything.Where(PipesIntoAnInterpreter).Select(s => DescribeSource(s, null)).FirstOrDefault();
        if (piped is not null)
        {
            return new ScriptDownloadAssessment(
                DownloadVerification.Unverified,
                $"This script pipes {piped} straight into a shell, so nothing can check it before it runs. Save it to a file first. "
                + HowToFix(language, platform));
        }

        var fetches = FindFetches(everything, code, powerShell);
        var installers = fetches.Where(f => IsRunOrInstalled(f, everything)).ToList();
        if (installers.Count == 0)
        {
            return new ScriptDownloadAssessment(DownloadVerification.NoDownload, null);
        }

        if (HasACheck(everything, powerShell))
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
    /// quotes and outside parentheses.</summary>
    private static IEnumerable<string> SplitStatements(string code, bool powerShell)
    {
        var current = new StringBuilder();
        char quote = '\0';
        var depth = 0;
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

            var isSeparator = depth == 0 && (c is '\n' or ';'
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
    /// <c>&lt;(...)</c>) or a parenthesised group becomes a nested statement.</summary>
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

        void Nest(string inner)
        {
            if (nesting < MaxNesting && inner.Trim().Length > 0)
            {
                stmt.Nested.Add(Parse(inner, powerShell, nesting + 1));
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
                    Flush();
                    Nest(inner);
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

    /// <summary>The command a segment runs, lower-cased and without its path or <c>.exe</c>: the first word that is
    /// not an environment assignment, a PowerShell assignment target, a shell keyword or <c>sudo</c> and its options.</summary>
    private static string? CommandWord(List<Token> segment)
    {
        var tokens = segment;
        for (var i = 0; i < tokens.Count; i++)
        {
            var text = tokens[i].Text;
            if (!tokens[i].Quoted && CommandPrefixes.Contains(text))
            {
                if (text.Equals("sudo", StringComparison.OrdinalIgnoreCase))
                {
                    while (i + 1 < tokens.Count && tokens[i + 1].Text.StartsWith('-') && !tokens[i + 1].Quoted)
                    {
                        i += SudoOptionsWithValue.Contains(tokens[i + 1].Text) ? 2 : 1;
                    }
                }

                continue;
            }

            if (!tokens[i].Quoted && ShellVariableAssignment.IsMatch(text))
            {
                continue;
            }

            if (text.StartsWith('$') && i + 1 < tokens.Count && tokens[i + 1].Text == "=" && !tokens[i + 1].Quoted)
            {
                i++;
                continue;
            }

            return Basename(text);
        }

        return null;
    }

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

        return false;
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
        }

        return fetches;
    }

    /// <summary>Whether a fetching segment writes the body to a file of its own accord, and which.</summary>
    private static (bool HasFile, string? Target, string? Url) WhereItWrites(List<Token> segment, string code)
    {
        var command = CommandWord(segment)!;
        var args = segment.Where(t => !t.Text.Equals(string.Empty)).ToList();
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
    /// again by a command that runs something. A fetch that is neither (a data file, an archive that is only
    /// stored) is not flagged.</summary>
    private static bool IsRunOrInstalled(Fetch fetch, List<Stmt> statements)
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
            return statements.Any(s => !ReferenceEquals(s, fetch.Statement) && RunsSomething(s));
        }

        var token = ReferenceToken(fetch.Target);
        var name = new Regex(@"(?<![\w.\-])" + Regex.Escape(token) + @"(?![\w.\-])", Insensitive, MatchTimeout);
        return statements.Any(s => !ReferenceEquals(s, fetch.Statement) && name.IsMatch(s.Raw) && RunsSomething(s));
    }

    /// <summary>What another line has to contain to be talking about the saved file: its file name when it has
    /// an extension, otherwise the whole path or variable.</summary>
    private static string ReferenceToken(string target)
    {
        var name = target.Replace('\\', '/');
        name = name[(name.LastIndexOf('/') + 1)..];
        return name.Contains('.') && !name.StartsWith('$') ? name : target;
    }

    private static readonly HashSet<string> RunCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "msiexec", "start-process", "installer", "hdiutil", "dpkg", "rpm", "apt", "apt-get", "dnf", "yum", "zypper",
        "chmod", "expand-archive", "unzip", "add-appxpackage", "add-appxprovisionedpackage", "bash", "sh", "zsh",
        "dash", "source", ".", "python", "python3", "perl", "ruby", "open", "invoke-item", "tar", "xattr"
    };

    private static bool RunsSomething(Stmt statement) =>
        Flatten(statement).Any(s => s.Segments.Any(SegmentRuns));

    private static bool SegmentRuns(List<Token> segment)
    {
        var command = CommandWord(segment);
        if (command is null)
        {
            return false;
        }

        if (RunCommands.Contains(command))
        {
            return command switch
            {
                // These only run something when they install, make executable or unpack: not when they
                // list, query or remove.
                "chmod" => segment.Any(t => t.Text.Contains("+x", StringComparison.Ordinal)),
                "hdiutil" => segment.Any(t => t.Text.Equals("attach", StringComparison.OrdinalIgnoreCase)),
                "dpkg" => segment.Any(t => t.Text is "-i" or "--install"),
                "rpm" => segment.Any(t => Regex.IsMatch(t.Text, @"^-[A-Za-z]*[iU][A-Za-z]*$|^--install$|^--upgrade$", RegexOptions.CultureInvariant, MatchTimeout)),
                "apt" or "apt-get" or "dnf" or "yum" or "zypper" => segment.Any(t => t.Text.Equals("install", StringComparison.OrdinalIgnoreCase)),
                "tar" => Unpacks(segment),
                "xattr" => false,
                _ => true
            };
        }

        // Running a path or a variable directly: ./installer, /opt/app/install, "$tmp/setup", & $out.
        return command.StartsWith("./", StringComparison.Ordinal) || command.StartsWith('/') || command.StartsWith('~')
            || command.StartsWith('$') || command.StartsWith("..", StringComparison.Ordinal);
    }

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
            return from.Trim('"', '\'', ')', '(', ',', '.', '`');
        }

        return string.IsNullOrEmpty(target) ? "a file" : target;
    }

    // ---- what counts as checking the download ----

    private static bool HasACheck(List<Stmt> statements, bool powerShell)
    {
        var hashVariables = new HashSet<string>(StringComparer.Ordinal);
        var signatureVariables = new HashSet<string>(StringComparer.Ordinal);
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
                if (hashVariables.Any(v => Refers(statement.Raw, v)))
                {
                    return true;
                }

                if (signatureVariables.Any(v => Refers(statement.Raw, v)) && ValidWord.IsMatch(statement.Raw))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>The statement and what it contains, minus anything that only prints: a string handed to echo,
    /// Write-Output and the like says what it likes without doing it. A print piped into something else
    /// (<c>echo "$sum  $file" | shasum -c -</c>) keeps what comes after the pipe.</summary>
    private static List<Stmt> Effective(Stmt statement)
    {
        var first = statement.Segments[0];
        var prints = IsPrint(first);
        if (!prints)
        {
            return Flatten(statement).ToList();
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
            return first[0].Text.StartsWith('$') && first.Count > 1 && first[1].Text == "=" ? first[0].Text.TrimStart('$') : null;
        }

        var match = Regex.Match(first[0].Text, @"^(?<name>[A-Za-z_]\w*)=", RegexOptions.CultureInvariant, MatchTimeout);
        return match.Success ? match.Groups["name"].Value : null;
    }

    private static bool Refers(string text, string variable) =>
        Regex.IsMatch(text, @"\$\{?" + Regex.Escape(variable) + @"(?![\w])", RegexOptions.CultureInvariant, MatchTimeout);
}
