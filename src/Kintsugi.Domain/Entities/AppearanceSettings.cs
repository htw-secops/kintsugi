using System.Text.RegularExpressions;
using Kintsugi.Domain.Common;
using Kintsugi.Domain.Exceptions;

namespace Kintsugi.Domain.Entities;

/// <summary>
/// Singleton: how a company has chosen the admin UI to look, for every administrator who signs in —
/// a colour and a picture behind the app. Read and written by the Appearance screen (<c>web/lib/presentation/settings/
/// appearance_screen.dart</c>); nothing else reads it.
/// </summary>
/// <remarks>
/// <para>
/// The organisation's name and logo are deliberately not here. They already live on
/// <see cref="PatchingPolicySettings"/> because the agents' end-user popups use them, and a second
/// copy would drift; the Appearance screen reads and writes them there.
/// </para>
/// <para>
/// This is presentation, not policy, like <see cref="PatchingPolicySettings.UpdateBranding"/>: nothing
/// about what Kintsugi does depends on it. So every value has a default that is exactly how the product
/// looks by default (dark, the HTW blue of the <c>kintsugi</c> preset, no picture), and a server with no row reports
/// those. Nothing here is a secret and nothing is sent to an agent.
/// </para>
/// <para>
/// The preset keys are not validated against a list: the presets (and their colours) are the client's,
/// and an unknown key is read there as the original palette, so a client newer than the server can add
/// one without the server refusing it. Only the shape of the key is checked.
/// </para>
/// </remarks>
public partial class AppearanceSettings : BaseEntity
{
    public const string DefaultColourMode = "dark";
    public const string DefaultPresetKey = "kintsugi";

    /// <summary>What the page does when no choice has been made, and the only values accepted. The
    /// client reads anything else as dark, so a value outside this list could never be shown.</summary>
    public static readonly IReadOnlyList<string> ColourModes = ["light", "dark", "system"];

    public const int MaxPresetKeyLength = 32;

    /// <summary>Largest wallpaper accepted, in bytes. It crosses the wire as base64 in every
    /// administrator's read of these settings, so it is kept to what a good photo compresses to
    /// (about 1.4 MB once encoded) rather than what a camera produces.</summary>
    public const int MaxWallpaperBytes = 1024 * 1024;

    public string ColourMode { get; private set; } = DefaultColourMode;

    /// <summary>A key into the client's preset colours, lower-case.</summary>
    public string PresetKey { get; private set; } = DefaultPresetKey;

    /// <summary><c>#RRGGBB</c>, upper-case, or null to use the preset. Wins over
    /// <see cref="PresetKey"/> when set. The client adjusts the shade so text stays readable on
    /// both themes, so this is the colour the company picked, not the one drawn.</summary>
    public string? AccentHex { get; private set; }

    /// <summary>PNG, JPEG or WebP drawn behind the app, or null for none. Crosses the wire as a
    /// base64 string, which is how System.Text.Json writes a <c>byte[]</c>. Checked by file signature,
    /// not by extension or declared type, which nothing here is told.</summary>
    public byte[]? Wallpaper { get; private set; }

    private AppearanceSettings()
    {
    }

    public static AppearanceSettings Create(
        string colourMode, string presetKey, string? accentHex, byte[]? wallpaper)
    {
        var settings = new AppearanceSettings();
        settings.Apply(colourMode, presetKey, accentHex, wallpaper);
        return settings;
    }

    public void Update(string colourMode, string presetKey, string? accentHex, byte[]? wallpaper)
    {
        Apply(colourMode, presetKey, accentHex, wallpaper);
        MarkUpdated();
    }

    /// <summary>True when <paramref name="text"/> is <c>#RRGGBB</c>. Public so the command validator
    /// can put the message under the field before the domain sees it.</summary>
    public static bool IsHexColour(string text) => HexColour().IsMatch(text);

    /// <summary>True when <paramref name="text"/> is a lower-case key of at most
    /// <see cref="MaxPresetKeyLength"/> letters, digits and hyphens.</summary>
    public static bool IsPresetKey(string text) => text.Length <= MaxPresetKeyLength && PresetKeyShape().IsMatch(text);

    /// <summary>Why <paramref name="bytes"/> cannot be the wallpaper, or null when they can. The same
    /// two rules <c>AppearanceSettings.wallpaperProblem</c> applies in the browser, which only
    /// saves the administrator a round trip: this is the check that counts.</summary>
    public static string? WallpaperProblem(byte[] bytes)
    {
        if (bytes.Length > MaxWallpaperBytes)
        {
            return $"The image must be {MaxWallpaperBytes / 1024 / 1024} MB or smaller.";
        }

        return IsPng(bytes) || IsJpeg(bytes) || IsWebP(bytes) ? null : "The image must be a PNG, JPEG or WebP file.";
    }

    private static bool IsPng(byte[] b) => b.AsSpan().StartsWith(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });

    private static bool IsJpeg(byte[] b) => b.AsSpan().StartsWith(new byte[] { 0xFF, 0xD8, 0xFF });

    // RIFF, a four-byte size, then WEBP. A RIFF file that is not WebP (a WAV, say) has the same
    // first four bytes, so the format tag has to be read too.
    private static bool IsWebP(byte[] b) =>
        b.Length >= 12 && b.AsSpan(0, 4).SequenceEqual("RIFF"u8) && b.AsSpan(8, 4).SequenceEqual("WEBP"u8);

    private void Apply(string colourMode, string presetKey, string? accentHex, byte[]? wallpaper)
    {
        var mode = (colourMode ?? string.Empty).Trim().ToLowerInvariant();
        if (!ColourModes.Contains(mode))
        {
            throw new DomainException("Colour mode must be light, dark or system.");
        }

        var preset = (presetKey ?? string.Empty).Trim().ToLowerInvariant();
        if (preset.Length == 0)
        {
            preset = DefaultPresetKey;
        }

        if (!IsPresetKey(preset))
        {
            throw new DomainException(
                $"The theme key can use lower-case letters, digits and hyphens, up to {MaxPresetKeyLength} characters.");
        }

        string? accent = null;
        if (!string.IsNullOrWhiteSpace(accentHex))
        {
            accent = accentHex.Trim();
            if (!IsHexColour(accent))
            {
                throw new DomainException("The accent colour must be six hex digits, for example #7C3AED.");
            }

            accent = accent.ToUpperInvariant();
        }

        var image = wallpaper is { Length: > 0 } ? wallpaper : null;
        if (image is not null && WallpaperProblem(image) is { } problem)
        {
            throw new DomainException(problem);
        }

        ColourMode = mode;
        PresetKey = preset;
        AccentHex = accent;
        Wallpaper = image;
    }

    [GeneratedRegex("^#[0-9A-Fa-f]{6}$")]
    private static partial Regex HexColour();

    [GeneratedRegex("^[a-z0-9-]+$")]
    private static partial Regex PresetKeyShape();
}
