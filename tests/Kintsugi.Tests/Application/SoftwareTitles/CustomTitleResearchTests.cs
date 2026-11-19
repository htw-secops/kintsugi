using MediatR;
using Moq;
using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Application.UpgradePaths;
using Kintsugi.Application.UpgradePaths.Commands.RefreshUpgradePath;
using Kintsugi.Application.UpgradePaths.Commands.ResearchApplicationUpgradePath;
using Kintsugi.Application.UpgradePaths.Queries.GetUpgradePathPrompt;
using Kintsugi.Application.UpgradePaths.Queries.PrepareUpgradePathScan;
using Kintsugi.Domain.Entities;
using Kintsugi.Domain.Enums;

namespace Kintsugi.Tests.Application.SoftwareTitles;

/// <summary>
/// The instructions panel on a custom software title that no host has installed yet: the prompt and
/// the refresh both used to answer "not currently installed on any host", which left nothing to
/// research an install script from.
/// </summary>
public class CustomTitleResearchTests
{
    private readonly Mock<ISender> _sender = new();
    private readonly Mock<IUpgradePathResearchClient> _research = new();
    private readonly Mock<ISoftwareTitleRepository> _titles = new();
    private readonly SoftwareTitle _title =
        SoftwareTitle.Create("Claude", "Windows", null, "Windows", null, null, SoftwareMode.Required, ["TEST_SECRET_A"], "admin");

    public CustomTitleResearchTests()
    {
        // Nothing in inventory anywhere: the plan has no work items.
        _sender.Setup(s => s.Send(It.IsAny<PrepareUpgradePathScanQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UpgradePathScanPlan(true, null, []));
        _titles.Setup(t => t.FindAsync("Claude", "Windows", It.IsAny<CancellationToken>())).ReturnsAsync(_title);
    }

    private GetUpgradePathPromptQueryHandler PromptHandler() => new(
        _sender.Object, _research.Object, new Mock<IUpgradePathRepository>().Object, new Mock<IPatchFailureRepository>().Object,
        new Mock<IHostRepository>().Object, new Mock<IManagedApplicationRepository>().Object, _titles.Object);

    [Fact]
    public async Task Prompt_ForACustomTitleNobodyHasYet_IsBuiltFromTheTitle_WithItsSecrets()
    {
        UpgradePathScriptGenerationRequest? asked = null;
        _research.Setup(r => r.BuildDefaultPrompt(It.IsAny<UpgradePathScriptGenerationRequest>()))
            .Callback<UpgradePathScriptGenerationRequest>(r => asked = r)
            .Returns("the prompt");

        var dto = await PromptHandler().Handle(new GetUpgradePathPromptQuery("Claude", "Windows"), CancellationToken.None);

        Assert.True(dto.Available);
        Assert.Equal("the prompt", dto.Prompt);
        Assert.Equal(("Claude", "Windows"), (asked!.ApplicationName, asked.Platform));
        Assert.Null(asked.ApplicationIdentifier);
        Assert.Equal(["TEST_SECRET_A"], asked.SecretNames);
    }

    [Fact]
    public async Task Prompt_WithNoPlatform_FindsTheCustomTitleOnWhicheverOs()
    {
        _research.Setup(r => r.BuildDefaultPrompt(It.IsAny<UpgradePathScriptGenerationRequest>())).Returns("the prompt");

        var dto = await PromptHandler().Handle(new GetUpgradePathPromptQuery("Claude", null), CancellationToken.None);

        Assert.True(dto.Available);
        Assert.Equal("Windows", dto.Platform);
    }

    [Fact]
    public async Task Prompt_ForAnAppWithNoTitleAndNoInstalls_StillSaysNotInstalled()
    {
        var dto = await PromptHandler().Handle(new GetUpgradePathPromptQuery("Nothing", "Windows"), CancellationToken.None);

        Assert.False(dto.Available);
        Assert.Contains("not currently installed", dto.Reason);
    }

    [Fact]
    public async Task Prompt_IgnoresAPackageManagerTitle_WhoseScriptIsTheManagers()
    {
        _titles.Setup(t => t.FindAsync("Greenshot", "Windows", It.IsAny<CancellationToken>()))
            .ReturnsAsync(SoftwareTitle.Create("Greenshot", "pm:winget", "winget", "Windows", "Greenshot.Greenshot", null, SoftwareMode.Required, [], "admin"));

        var dto = await PromptHandler().Handle(new GetUpgradePathPromptQuery("Greenshot", "Windows"), CancellationToken.None);

        Assert.False(dto.Available);
    }

    [Fact]
    public async Task Refresh_ForACustomTitleNobodyHasYet_ResearchesIt_WithItsSecretsAndThePastedInstructions()
    {
        ResearchApplicationUpgradePathCommand? sent = null;
        _sender.Setup(s => s.Send(It.IsAny<ResearchApplicationUpgradePathCommand>(), It.IsAny<CancellationToken>()))
            .Callback<IRequest<ResearchApplicationUpgradePathResult>, CancellationToken>((c, _) => sent = (ResearchApplicationUpgradePathCommand)c)
            .ReturnsAsync(new ResearchApplicationUpgradePathResult("Claude", "Windows", UpgradePathStatus.Found, false, null));

        var result = await new RefreshUpgradePathCommandHandler(_sender.Object, _titles.Object)
            .Handle(new RefreshUpgradePathCommand("Claude", "Windows", "Convert this Ninja script: ..."), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(("Claude", "Windows", UpgradePathWorkKind.Research), (sent!.ApplicationName, sent.Platform, sent.Kind));
        Assert.Equal("Convert this Ninja script: ...", sent.PromptOverride);
        Assert.Equal(["TEST_SECRET_A"], sent.SecretNames);
    }
}
