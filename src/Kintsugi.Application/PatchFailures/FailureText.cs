using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Kintsugi.Application.PatchFailures;

/// <summary>
/// Two jobs on a failing script's output, kept in one place so they cannot drift apart:
/// <see cref="Normalize"/> flattens the parts that differ between machines so the same failure
/// looks the same everywhere (the failure signature and the AI answer cache both hash it), and
/// <see cref="ScrubForAi"/> removes what must not leave the server before the text is sent to a
/// model. Only the *sent copy* is scrubbed; the stored details stay as reported.
/// </summary>
public static class FailureText
{
    /// <summary>How much of the output is kept for hashing and for sending — the tail, where the
    /// error usually is.</summary>
    public const int MaxChars = 2000;

    private static readonly Regex Guid_ = new(@"\b[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Email = new(@"[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}", RegexOptions.Compiled);
    private static readonly Regex Ip = new(@"\b\d{1,3}(?:\.\d{1,3}){3}\b", RegexOptions.Compiled);
    private static readonly Regex LongHex = new(@"\b(?:0x)?[0-9a-f]{12,}\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex WindowsProfile = new(@"([A-Za-z]:[\\/]+Users[\\/]+)[^\\/\s""':]+", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex MacProfile = new(@"(/Users/)[^/\s""':]+", RegexOptions.Compiled);
    private static readonly Regex LinuxProfile = new(@"(/home/)[^/\s""':]+", RegexOptions.Compiled);
    private static readonly Regex HexNumber = new(@"0x[0-9a-f]+", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Digits = new(@"\d+", RegexOptions.Compiled);
    private static readonly Regex Spaces = new(@"\s+", RegexOptions.Compiled);

    /// <summary>The text with machine-specific parts flattened: profile names, GUIDs, hex, numbers
    /// (so versions and ports) and whitespace. Lower case; the tail of at most <see cref="MaxChars"/>.</summary>
    public static string Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var t = text.ToLowerInvariant();
        t = WindowsProfile.Replace(t, "$1<user>");
        t = MacProfile.Replace(t, "$1<user>");
        t = LinuxProfile.Replace(t, "$1<user>");
        t = Guid_.Replace(t, "<id>");
        t = HexNumber.Replace(t, "<n>");
        t = Digits.Replace(t, "<n>");
        t = Spaces.Replace(t, " ").Trim();
        return t.Length <= MaxChars ? t : t[^MaxChars..];
    }

    /// <summary>
    /// A stable id for "this same problem": SHA-256 (hex, lower case, first 32 characters) of the
    /// application, the category and the normalised output. A real hash, never
    /// <c>string.GetHashCode</c>, which differs per process and would break stored signatures.
    /// </summary>
    public static string Signature(string applicationName, string? category, string? details) =>
        Hash(applicationName.Trim().ToLowerInvariant() + "\n" + (category ?? string.Empty) + "\n" + Normalize(details));

    /// <summary>The key the AI answer cache uses: the same normaliser, without the category (it is
    /// what is being asked for).</summary>
    public static string AiCacheKey(string applicationName, string? details) =>
        Hash(applicationName.Trim().ToLowerInvariant() + "\n" + Normalize(details));

    private static string Hash(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant()[..32];

    /// <summary>
    /// The text that may be sent to a model: the tail of the output with this host's name and serial,
    /// profile folder names, emails, IPs, GUIDs and long hex tokens replaced by placeholders.
    /// </summary>
    public static string ScrubForAi(string details, string? hostname, string? serial)
    {
        var t = details.Length <= MaxChars ? details : details[^MaxChars..];

        foreach (var secret in new[] { hostname, serial })
        {
            if (!string.IsNullOrWhiteSpace(secret) && secret.Trim().Length >= 3)
            {
                t = Regex.Replace(t, Regex.Escape(secret.Trim()), secret == serial ? "<serial>" : "<host>", RegexOptions.IgnoreCase);
            }
        }

        t = WindowsProfile.Replace(t, "$1<user>");
        t = MacProfile.Replace(t, "$1<user>");
        t = LinuxProfile.Replace(t, "$1<user>");
        t = Email.Replace(t, "<email>");
        t = Guid_.Replace(t, "<id>");
        t = Ip.Replace(t, "<ip>");
        t = LongHex.Replace(t, "<hex>");
        return t;
    }
}
