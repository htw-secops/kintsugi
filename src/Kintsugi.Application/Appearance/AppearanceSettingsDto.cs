namespace Kintsugi.Application.Appearance;

/// <summary>
/// How the admin UI looks for this company. Mirrors <c>AppearanceSettings</c> in
/// <c>web/lib/domain/entities/appearance.dart</c> and its mapper in
/// <c>web/lib/data/repositories/appearance_repository_impl.dart</c>, by hand — change one, change both.
/// </summary>
/// <param name="ColourMode"><c>light</c>, <c>dark</c> or <c>system</c>.</param>
/// <param name="PresetKey">A key into the client's preset colours.</param>
/// <param name="AccentHex"><c>#RRGGBB</c>, or null to use the preset.</param>
/// <param name="Wallpaper">PNG, JPEG or WebP, or null. A <c>byte[]</c>, so it is a base64 string on
/// the wire; at most 1 MB, so up to about 1.4 MB there.</param>
public record AppearanceSettingsDto(
    string ColourMode,
    string PresetKey,
    string? AccentHex,
    byte[]? Wallpaper);
