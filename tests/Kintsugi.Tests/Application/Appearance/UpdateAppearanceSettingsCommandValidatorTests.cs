using FluentValidation.TestHelper;
using Kintsugi.Application.Appearance.Commands.UpdateAppearanceSettings;
using Kintsugi.Domain.Entities;
using Kintsugi.Tests.Domain;

namespace Kintsugi.Tests.Application.Appearance;

public class UpdateAppearanceSettingsCommandValidatorTests
{
    private readonly UpdateAppearanceSettingsCommandValidator _validator = new();

    private static UpdateAppearanceSettingsCommand Valid(
        string colourMode = "dark", string presetKey = "kintsugi", string? accentHex = null,
        byte[]? wallpaper = null, string logoAnimation = "spin") =>
        new(colourMode, presetKey, accentHex, wallpaper, logoAnimation);

    [Fact]
    public void ValidCommand_Passes()
    {
        _validator.TestValidate(Valid()).ShouldNotHaveAnyValidationErrors();
        _validator.TestValidate(Valid("light", "gold", "#F2B01E", AppearanceSettingsTests.Png(), "fade"))
            .ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void ColourMode_Unknown_IsRejected_UnderItsOwnProperty() =>
        _validator.TestValidate(Valid(colourMode: "neon")).ShouldHaveValidationErrorFor(c => c.ColourMode);

    [Fact]
    public void PresetKey_OfTheWrongShape_IsRejected() =>
        _validator.TestValidate(Valid(presetKey: "Has Space")).ShouldHaveValidationErrorFor(c => c.PresetKey);

    [Fact]
    public void PresetKey_Blank_IsAccepted_AsTheDefault() =>
        _validator.TestValidate(Valid(presetKey: "")).ShouldNotHaveValidationErrorFor(c => c.PresetKey);

    [Theory]
    [InlineData("0E8ABD")]
    [InlineData("#12345")]
    [InlineData("blue")]
    public void AccentHex_Malformed_IsRejected(string hex) =>
        _validator.TestValidate(Valid(accentHex: hex)).ShouldHaveValidationErrorFor(c => c.AccentHex);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void AccentHex_Blank_IsAccepted(string? hex) =>
        _validator.TestValidate(Valid(accentHex: hex)).ShouldNotHaveValidationErrorFor(c => c.AccentHex);

    [Fact]
    public void Wallpaper_NotAnImage_IsRejected_WithTheReason()
    {
        var result = _validator.TestValidate(Valid(wallpaper: new byte[] { 1, 2, 3, 4 }));

        result.ShouldHaveValidationErrorFor(c => c.Wallpaper).WithErrorMessage("The image must be a PNG, JPEG or WebP file.");
    }

    [Fact]
    public void Wallpaper_OverTheLimit_IsRejected_WithTheReason()
    {
        var result = _validator.TestValidate(Valid(wallpaper: AppearanceSettingsTests.Png(AppearanceSettings.MaxWallpaperBytes + 1)));

        result.ShouldHaveValidationErrorFor(c => c.Wallpaper).WithErrorMessage("The image must be 1 MB or smaller.");
    }

    [Fact]
    public void Wallpaper_NullOrEmpty_IsAccepted()
    {
        _validator.TestValidate(Valid(wallpaper: null)).ShouldNotHaveValidationErrorFor(c => c.Wallpaper);
        _validator.TestValidate(Valid(wallpaper: Array.Empty<byte>())).ShouldNotHaveValidationErrorFor(c => c.Wallpaper);
    }

    [Fact]
    public void LogoAnimation_Unknown_IsRejected() =>
        _validator.TestValidate(Valid(logoAnimation: "flip")).ShouldHaveValidationErrorFor(c => c.LogoAnimation);
}
