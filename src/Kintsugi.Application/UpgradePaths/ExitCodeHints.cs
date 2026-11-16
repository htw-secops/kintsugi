using System.Globalization;
using System.Text.RegularExpressions;

namespace Kintsugi.Application.UpgradePaths;

/// <summary>
/// Turns the exit codes winget (and the Windows installer stack under it) reports into the form a
/// person can search for, and says in a few words what the well-known ones mean.
/// </summary>
/// <remarks>
/// <para>
/// winget exits with an HRESULT, and PowerShell hands it back as a *signed* 32-bit integer, so a
/// failure reads <c>-1978335138</c> in a log while every document, forum thread and error table
/// calls it <c>0x8A15005E</c>. Nobody can search for the first. This class converts one to the
/// other and carries the short hint for the codes that turned up in real failures.
/// </para>
/// <para>
/// <see cref="Known"/> is also what <see cref="WingetUpgradeScript"/> is built from, so the hint a
/// person reads in the script's own error output and the hint the server shows next to a stored
/// failure come from one table and cannot drift apart. A new code is one line here.
/// </para>
/// </remarks>
public static class ExitCodeHints
{
    /// <summary>One code and what it means, in a sentence short enough for a table cell.</summary>
    /// <param name="Code">The HRESULT, as the unsigned value documents print.</param>
    /// <param name="Hint">What it usually means and what to look at.</param>
    public sealed record ExitCodeHint(uint Code, string Hint);

    /// <summary>The codes worth explaining. Order is only for readability.</summary>
    public static IReadOnlyList<ExitCodeHint> Known { get; } =
    [
        new(0x8A15005E, "msstore certificate pinning failed: outdated winget or SSL inspection"),
        new(0x80070057, "Invalid parameter: often an MSIX framework package or an app already current"),
        // Not a failure — winget's way of saying the package is already current — but it shows up in
        // logs and is worth reading correctly rather than as a bare number.
        new(0x8A15002B, "No applicable upgrade found: the package is already current"),
    ];

    /// <summary>
    /// The code in the form documents print: <c>0x</c> and eight upper-case hex digits. A negative
    /// <see cref="int"/> is reinterpreted, not negated: <c>-1978335138</c> is <c>0x8A15005E</c>.
    /// </summary>
    public static string ToHex(int code) => ToHex(unchecked((uint)code));

    /// <inheritdoc cref="ToHex(int)"/>
    public static string ToHex(uint code) => "0x" + code.ToString("X8", CultureInfo.InvariantCulture);

    /// <summary>
    /// Writes the code as hex and, when it is one of the <see cref="Known"/> ones, its hint.
    /// Returns whether a hint was found; <paramref name="hex"/> is always set.
    /// </summary>
    public static bool TryExplain(int code, out string hex, out string? hint) =>
        TryExplain(unchecked((uint)code), out hex, out hint);

    /// <inheritdoc cref="TryExplain(int, out string, out string?)"/>
    public static bool TryExplain(uint code, out string hex, out string? hint)
    {
        hex = ToHex(code);
        hint = Known.FirstOrDefault(k => k.Code == code)?.Hint;
        return hint is not null;
    }

    // 0x followed by exactly eight hex digits, as HRESULTs are printed. Not preceded by a word
    // character, so the tail of a longer hex string (a hash, a GUID) is not read as a code.
    private static readonly Regex HexCode = new(@"(?<![0-9A-Za-z])0x([0-9A-Fa-f]{8})(?![0-9A-Za-z])", RegexOptions.Compiled);

    // A negative decimal of six or more digits: what PowerShell prints for an HRESULT. The length
    // floor is what keeps `exit -1` and a command-line flag like `-5` from being read as one.
    private static readonly Regex NegativeDecimal = new(@"(?<![0-9A-Za-z.\-])-(\d{6,10})(?![0-9A-Za-z.])", RegexOptions.Compiled);

    /// <summary>
    /// Finds the first exit code in a piece of output — written as <c>0x........</c> or as a
    /// negative decimal — and describes it: <c>0x8A15005E (hint)</c> for a known one, just the hex
    /// for an unknown one, null when the text holds no code at all.
    /// </summary>
    /// <remarks>
    /// The earliest code in the text wins, whichever way it is written, because the first one a
    /// script printed is the one that stopped it.
    /// </remarks>
    public static string? Describe(string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return null;
        }

        uint? best = null;
        var bestIndex = int.MaxValue;

        var hex = HexCode.Match(output);
        if (hex.Success)
        {
            best = uint.Parse(hex.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            bestIndex = hex.Index;
        }

        var negative = NegativeDecimal.Match(output);
        if (negative.Success && negative.Index < bestIndex
            && long.TryParse(negative.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var magnitude)
            && magnitude <= 2_147_483_648L)
        {
            best = unchecked((uint)(int)(-magnitude));
        }

        if (best is not { } code)
        {
            return null;
        }

        return TryExplain(code, out var text, out var hint) ? $"{text} ({hint})" : text;
    }
}
