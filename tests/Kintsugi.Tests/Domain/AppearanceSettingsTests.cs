using Kintsugi.Domain.Entities;
using Kintsugi.Domain.Exceptions;

namespace Kintsugi.Tests.Domain;

public class AppearanceSettingsTests
{
    internal static byte[] Png(int length = 16) => PatchingPolicySettingsTests.Png(length);

    internal static byte[] Jpeg(int length = 16)
    {
        var bytes = new byte[length];
        new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 }.CopyTo(bytes, 0);
        return bytes;
    }

    // RIFF, a size, then the format tag. A WAV starts the same four bytes and differs only in the tag.
    internal static byte[] Riff(string format, int length = 16)
    {
        var bytes = new byte[length];
        "RIFF"u8.ToArray().CopyTo(bytes, 0);
        System.Text.Encoding.ASCII.GetBytes(format).CopyTo(bytes, 8);
        return bytes;
    }

    private static AppearanceSettings Default() =>
        AppearanceSettings.Create("dark", "kintsugi", null, null);

    [Fact]
    public void Create_SetsAllFields()
    {
        var png = Png();

        var settings = AppearanceSettings.Create("light", "gold", "#7c3aed", png);

        Assert.Equal("light", settings.ColourMode);
        Assert.Equal("gold", settings.PresetKey);
        Assert.Equal("#7C3AED", settings.AccentHex);
        Assert.Equal(png, settings.Wallpaper);
    }

    [Fact]
    public void TheDefaultsAreHowTheProductLooksOutOfTheBox()
    {
        Assert.Equal("dark", AppearanceSettings.DefaultColourMode);
        Assert.Equal("kintsugi", AppearanceSettings.DefaultPresetKey);
    }

    [Theory]
    [InlineData("light")]
    [InlineData("dark")]
    [InlineData("system")]
    [InlineData(" Light ")]
    public void ColourMode_AcceptsTheThreeModes_IgnoringCaseAndSpaces(string mode)
    {
        var settings = AppearanceSettings.Create(mode, "kintsugi", null, null);

        Assert.Equal(mode.Trim().ToLowerInvariant(), settings.ColourMode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("auto")]
    [InlineData("night")]
    public void ColourMode_RejectsAnythingElse(string mode)
    {
        Assert.Throws<DomainException>(() => AppearanceSettings.Create(mode, "kintsugi", null, null));
    }

    [Theory]
    [InlineData("#0E8ABD", "#0E8ABD")]
    [InlineData("#0e8abd", "#0E8ABD")]
    [InlineData("  #7c3aed ", "#7C3AED")]
    public void AccentHex_IsStoredUpperCase(string given, string stored)
    {
        Assert.Equal(stored, AppearanceSettings.Create("dark", "kintsugi", given, null).AccentHex);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AccentHex_BlankMeansUseThePreset(string? given)
    {
        Assert.Null(AppearanceSettings.Create("dark", "kintsugi", given, null).AccentHex);
    }

    [Theory]
    [InlineData("0E8ABD")]
    [InlineData("#0E8AB")]
    [InlineData("#0E8ABDD")]
    [InlineData("#GGGGGG")]
    [InlineData("red")]
    public void AccentHex_RejectsAnythingButSixHexDigitsWithAHash(string given)
    {
        Assert.Throws<DomainException>(() => AppearanceSettings.Create("dark", "kintsugi", given, null));
    }

    [Fact]
    public void PresetKey_IsLowerCased_AndBlankFallsBackToTheDefault()
    {
        Assert.Equal("gold", AppearanceSettings.Create("dark", " Gold ", null, null).PresetKey);
        Assert.Equal("kintsugi", AppearanceSettings.Create("dark", "  ", null, null).PresetKey);
    }

    [Theory]
    [InlineData("has space")]
    [InlineData("under_score")]
    [InlineData("a/b")]
    [InlineData("0123456789012345678901234567890123")]
    public void PresetKey_RejectsAKeyOfTheWrongShape(string key)
    {
        Assert.Throws<DomainException>(() => AppearanceSettings.Create("dark", key, null, null));
    }

    [Fact]
    public void PresetKey_IsNotCheckedAgainstAList_SoANewerClientCanAddOne()
    {
        Assert.Equal("a-future-theme", AppearanceSettings.Create("dark", "a-future-theme", null, null).PresetKey);
    }

    [Fact]
    public void Wallpaper_AcceptsPngJpegAndWebP()
    {
        Assert.NotNull(AppearanceSettings.Create("dark", "kintsugi", null, Png()).Wallpaper);
        Assert.NotNull(AppearanceSettings.Create("dark", "kintsugi", null, Jpeg()).Wallpaper);
        Assert.NotNull(AppearanceSettings.Create("dark", "kintsugi", null, Riff("WEBP")).Wallpaper);
    }

    [Fact]
    public void Wallpaper_RejectsARiffFileThatIsNotWebP()
    {
        var ex = Assert.Throws<DomainException>(() =>
            AppearanceSettings.Create("dark", "kintsugi", null, Riff("WAVE")));

        Assert.Contains("PNG, JPEG or WebP", ex.Message);
    }

    [Fact]
    public void Wallpaper_RejectsAFileThatIsNotAnImage_ByItsSignatureNotItsName()
    {
        var gif = new byte[] { 0x47, 0x49, 0x46, 0x38, 0x39, 0x61, 0, 0 };

        Assert.Throws<DomainException>(() => AppearanceSettings.Create("dark", "kintsugi", null, gif));
        Assert.Throws<DomainException>(() => AppearanceSettings.Create("dark", "kintsugi", null, new byte[] { 1, 2, 3 }));
    }

    [Fact]
    public void Wallpaper_RejectsAFileOverTheLimit_AndAcceptsOneExactlyAtIt()
    {
        Assert.NotNull(AppearanceSettings.Create(
            "dark", "kintsugi", null, Png(AppearanceSettings.MaxWallpaperBytes)).Wallpaper);

        var ex = Assert.Throws<DomainException>(() => AppearanceSettings.Create(
            "dark", "kintsugi", null, Png(AppearanceSettings.MaxWallpaperBytes + 1)));
        Assert.Contains("1 MB", ex.Message);
    }

    [Fact]
    public void Wallpaper_NullOrEmptyMeansNone()
    {
        Assert.Null(AppearanceSettings.Create("dark", "kintsugi", null, null).Wallpaper);
        Assert.Null(AppearanceSettings.Create("dark", "kintsugi", null, Array.Empty<byte>()).Wallpaper);
    }

    [Fact]
    public void Update_ReplacesEverything_AndMarksItUpdated()
    {
        var settings = Default();
        Assert.Null(settings.UpdatedAtUtc);

        settings.Update("light", "ocean", "#3B82F6", Png());

        Assert.Equal("light", settings.ColourMode);
        Assert.Equal("ocean", settings.PresetKey);
        Assert.Equal("#3B82F6", settings.AccentHex);
        Assert.NotNull(settings.Wallpaper);
        Assert.NotNull(settings.UpdatedAtUtc);
    }

    [Fact]
    public void Update_WithNullWallpaperAndAccent_RemovesThem()
    {
        var settings = AppearanceSettings.Create("dark", "kintsugi", "#3B82F6", Png());

        settings.Update("dark", "kintsugi", null, null);

        Assert.Null(settings.AccentHex);
        Assert.Null(settings.Wallpaper);
    }

    [Fact]
    public void ARefusedUpdate_ChangesNothing()
    {
        var settings = AppearanceSettings.Create("light", "gold", "#F2B01E", Png());

        Assert.Throws<DomainException>(() => settings.Update("light", "ocean", "#3B82F6", new byte[] { 1 }));

        Assert.Equal("gold", settings.PresetKey);
        Assert.Equal("#F2B01E", settings.AccentHex);
        Assert.Null(settings.UpdatedAtUtc);
    }
}
