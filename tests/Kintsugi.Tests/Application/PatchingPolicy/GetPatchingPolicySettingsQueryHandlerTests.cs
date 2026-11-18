using Moq;
using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Application.PatchingPolicy.Queries.GetPatchingPolicySettings;
using Kintsugi.Domain.Entities;
using Kintsugi.Domain.Enums;

namespace Kintsugi.Tests.Application.PatchingPolicy;

public class GetPatchingPolicySettingsQueryHandlerTests
{
    private readonly Mock<IPatchingPolicySettingsRepository> _repository = new();

    private GetPatchingPolicySettingsQueryHandler CreateHandler() => new(_repository.Object);

    [Fact]
    public async Task Handle_WhenNoneSavedYet_ReturnsSensibleDefaults()
    {
        _repository.Setup(r => r.GetAsync(It.IsAny<CancellationToken>())).ReturnsAsync((PatchingPolicySettings?)null);

        var result = await CreateHandler().Handle(new GetPatchingPolicySettingsQuery(), CancellationToken.None);

        Assert.Equal(7, result.IntervalValue);
        Assert.Equal(PatchingTimeUnit.Days, result.IntervalUnit);
        Assert.Null(result.OrganisationName);
        Assert.Null(result.OrganisationLogoPng);
    }

    [Fact]
    public async Task Handle_WhenSettingsExist_ReturnsThem()
    {
        var settings = PatchingPolicySettings.Create(1, PatchingTimeUnit.Hours, 4, PatchingTimeUnit.Hours, 0);
        _repository.Setup(r => r.GetAsync(It.IsAny<CancellationToken>())).ReturnsAsync(settings);

        var result = await CreateHandler().Handle(new GetPatchingPolicySettingsQuery(), CancellationToken.None);

        Assert.Equal(1, result.IntervalValue);
        Assert.Equal(PatchingTimeUnit.Hours, result.IntervalUnit);
        Assert.Equal(0, result.MaxDelayCount);
    }

    // The agent-facing contract the Windows agent's popups read: these two camelCase keys, the logo
    // as base64, and null (not absent) when unset. Serialised with the same Web defaults the
    // controllers use.
    [Fact]
    public async Task Handle_SerialisesTheBrandingAsTheAgentsReadIt()
    {
        var settings = PatchingPolicySettings.Create(7, PatchingTimeUnit.Days, 1, PatchingTimeUnit.Days, 3);
        settings.UpdateBranding("Example Corp IT", [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        _repository.Setup(r => r.GetAsync(It.IsAny<CancellationToken>())).ReturnsAsync(settings);

        var result = await CreateHandler().Handle(new GetPatchingPolicySettingsQuery(), CancellationToken.None);
        using var json = System.Text.Json.JsonDocument.Parse(
            System.Text.Json.JsonSerializer.Serialize(result, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)));

        Assert.Equal("Example Corp IT", json.RootElement.GetProperty("organisationName").GetString());
        Assert.Equal("iVBORw0KGgo=", json.RootElement.GetProperty("organisationLogoPng").GetString());
    }

    [Fact]
    public async Task Handle_SerialisesAbsentBrandingAsNull()
    {
        _repository.Setup(r => r.GetAsync(It.IsAny<CancellationToken>())).ReturnsAsync((PatchingPolicySettings?)null);

        var result = await CreateHandler().Handle(new GetPatchingPolicySettingsQuery(), CancellationToken.None);
        using var json = System.Text.Json.JsonDocument.Parse(
            System.Text.Json.JsonSerializer.Serialize(result, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)));

        Assert.Equal(System.Text.Json.JsonValueKind.Null, json.RootElement.GetProperty("organisationName").ValueKind);
        Assert.Equal(System.Text.Json.JsonValueKind.Null, json.RootElement.GetProperty("organisationLogoPng").ValueKind);
    }
}
