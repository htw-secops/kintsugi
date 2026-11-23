using Moq;
using Kintsugi.Application.Appearance.Commands.UpdateAppearanceSettings;
using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Domain.Entities;
using Kintsugi.Domain.Exceptions;
using Kintsugi.Tests.Domain;

namespace Kintsugi.Tests.Application.Appearance;

public class UpdateAppearanceSettingsCommandHandlerTests
{
    private readonly Mock<IAppearanceSettingsRepository> _repository = new();
    private readonly Mock<IUnitOfWork> _unitOfWork = new();

    private UpdateAppearanceSettingsCommandHandler CreateHandler() => new(_repository.Object, _unitOfWork.Object);

    [Fact]
    public async Task Handle_WhenNothingIsSavedYet_CreatesTheRow()
    {
        AppearanceSettings? added = null;
        _repository.Setup(r => r.GetAsync(It.IsAny<CancellationToken>())).ReturnsAsync((AppearanceSettings?)null);
        _repository.Setup(r => r.AddAsync(It.IsAny<AppearanceSettings>(), It.IsAny<CancellationToken>()))
            .Callback<AppearanceSettings, CancellationToken>((s, _) => added = s);
        var png = AppearanceSettingsTests.Png();

        var result = await CreateHandler().Handle(
            new UpdateAppearanceSettingsCommand("light", "gold", "#f2b01e", png, "fade"), CancellationToken.None);

        Assert.Equal("light", result.ColourMode);
        Assert.Equal("gold", result.PresetKey);
        Assert.Equal("#F2B01E", result.AccentHex);
        Assert.Equal(png, result.Wallpaper);
        Assert.Equal("fade", result.LogoAnimation);
        Assert.Equal("fade", added!.LogoAnimation);
        _unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Handle_WhenARowExists_UpdatesItInPlace()
    {
        var existing = AppearanceSettings.Create("dark", "kintsugi", null, null, "spin");
        _repository.Setup(r => r.GetAsync(It.IsAny<CancellationToken>())).ReturnsAsync(existing);

        var result = await CreateHandler().Handle(
            new UpdateAppearanceSettingsCommand("system", "ocean", null, null, "off"), CancellationToken.None);

        Assert.Equal("system", result.ColourMode);
        Assert.Equal("system", existing.ColourMode);
        Assert.Equal("off", existing.LogoAnimation);
        _repository.Verify(r => r.AddAsync(It.IsAny<AppearanceSettings>(), It.IsAny<CancellationToken>()), Times.Never);
        _unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Handle_WithNoWallpaperAndNoAccent_RemovesThemFromAnExistingRow()
    {
        var existing = AppearanceSettings.Create("dark", "kintsugi", "#3B82F6", AppearanceSettingsTests.Png(), "spin");
        _repository.Setup(r => r.GetAsync(It.IsAny<CancellationToken>())).ReturnsAsync(existing);

        var result = await CreateHandler().Handle(
            new UpdateAppearanceSettingsCommand("dark", "kintsugi", null, null, "spin"), CancellationToken.None);

        Assert.Null(result.AccentHex);
        Assert.Null(result.Wallpaper);
        Assert.Null(existing.Wallpaper);
    }

    [Fact]
    public async Task Handle_RefusesAnInvalidValue_AndSavesNothing()
    {
        _repository.Setup(r => r.GetAsync(It.IsAny<CancellationToken>())).ReturnsAsync((AppearanceSettings?)null);

        await Assert.ThrowsAsync<DomainException>(() => CreateHandler().Handle(
            new UpdateAppearanceSettingsCommand("neon", "kintsugi", null, null, "spin"), CancellationToken.None));

        _unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }
}
