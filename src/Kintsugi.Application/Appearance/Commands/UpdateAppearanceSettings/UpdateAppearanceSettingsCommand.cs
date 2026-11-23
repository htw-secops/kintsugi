using MediatR;

namespace Kintsugi.Application.Appearance.Commands.UpdateAppearanceSettings;

/// <summary>Replaces the whole appearance, as the screen sends it: a null or empty
/// <paramref name="Wallpaper"/> removes the picture, a blank <paramref name="AccentHex"/> goes back to
/// the preset.</summary>
public record UpdateAppearanceSettingsCommand(
    string ColourMode,
    string PresetKey,
    string? AccentHex,
    byte[]? Wallpaper,
    string LogoAnimation) : IRequest<AppearanceSettingsDto>;
