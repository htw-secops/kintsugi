using Moq;
using Kintsugi.Application.Appearance.Queries.GetAppearanceSettings;
using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Domain.Entities;
using Kintsugi.Tests.Domain;

namespace Kintsugi.Tests.Application.Appearance;

public class GetAppearanceSettingsQueryHandlerTests
{
    private readonly Mock<IAppearanceSettingsRepository> _repository = new();

    private GetAppearanceSettingsQueryHandler CreateHandler() => new(_repository.Object);

    [Fact]
    public async Task Handle_WithNothingSaved_AnswersHowTheProductLookedBeforeThisExisted()
    {
        _repository.Setup(r => r.GetAsync(It.IsAny<CancellationToken>())).ReturnsAsync((AppearanceSettings?)null);

        var result = await CreateHandler().Handle(new GetAppearanceSettingsQuery(), CancellationToken.None);

        Assert.Equal("dark", result.ColourMode);
        Assert.Equal("kintsugi", result.PresetKey);
        Assert.Null(result.AccentHex);
        Assert.Null(result.Wallpaper);
    }

    [Fact]
    public async Task Handle_ReturnsWhatIsStored()
    {
        var png = AppearanceSettingsTests.Png();
        _repository.Setup(r => r.GetAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(AppearanceSettings.Create("light", "gold", "#F2B01E", png));

        var result = await CreateHandler().Handle(new GetAppearanceSettingsQuery(), CancellationToken.None);

        Assert.Equal("light", result.ColourMode);
        Assert.Equal("gold", result.PresetKey);
        Assert.Equal("#F2B01E", result.AccentHex);
        Assert.Equal(png, result.Wallpaper);
    }

    // The Flutter client reads these exact names (web/lib/data/repositories/appearance_repository_impl.dart)
    // and nothing else checks that the two sides agree: the clients are compiled separately.
    [Fact]
    public void TheDto_SerialisesToTheFieldNamesTheWebClientReads_WithTheWallpaperAsBase64()
    {
        var dto = new Kintsugi.Application.Appearance.AppearanceSettingsDto(
            "light", "gold", "#F2B01E", new byte[] { 0x89, 0x50, 0x4E, 0x47 });

        var json = System.Text.Json.JsonSerializer.Serialize(dto, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));

        Assert.Equal(
            "{\"colourMode\":\"light\",\"presetKey\":\"gold\",\"accentHex\":\"#F2B01E\",\"wallpaper\":\"iVBORw==\"}",
            json);
    }
}
