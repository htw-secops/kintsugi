using FluentValidation;
using Kintsugi.Domain.Entities;

namespace Kintsugi.Application.Appearance.Commands.UpdateAppearanceSettings;

/// <summary>
/// Duplicates the rules <c>AppearanceSettings.Apply</c> enforces, deliberately — the same split
/// <c>UpdateVulnerabilitySettingsCommandValidator</c> exists for. The entity keeps the invariant true
/// whoever writes to it; this puts the message under the field that caused it, keyed by C# property
/// name, which is how a field error finds its box in the Flutter form.
/// </summary>
public class UpdateAppearanceSettingsCommandValidator : AbstractValidator<UpdateAppearanceSettingsCommand>
{
    public UpdateAppearanceSettingsCommandValidator()
    {
        RuleFor(x => x.ColourMode)
            .Must(m => AppearanceSettings.ColourModes.Contains((m ?? string.Empty).Trim().ToLowerInvariant()))
            .WithMessage("Colour mode must be light, dark or system.");

        RuleFor(x => x.PresetKey)
            .Must(k => string.IsNullOrWhiteSpace(k) || AppearanceSettings.IsPresetKey(k.Trim().ToLowerInvariant()))
            .WithMessage(
                $"The theme key can use lower-case letters, digits and hyphens, up to {AppearanceSettings.MaxPresetKeyLength} characters.");

        RuleFor(x => x.AccentHex)
            .Must(h => AppearanceSettings.IsHexColour(h!.Trim()))
            .When(x => !string.IsNullOrWhiteSpace(x.AccentHex))
            .WithMessage("The accent colour must be six hex digits, for example #7C3AED.");

        // The size limit and the file-type check are one message each, from the same function the
        // entity uses, so the two cannot disagree about what is allowed.
        RuleFor(x => x.Wallpaper)
            .Custom((wallpaper, context) =>
            {
                if (AppearanceSettings.WallpaperProblem(wallpaper!) is { } problem)
                {
                    context.AddFailure(problem);
                }
            })
            .When(x => x.Wallpaper is { Length: > 0 });
    }
}
