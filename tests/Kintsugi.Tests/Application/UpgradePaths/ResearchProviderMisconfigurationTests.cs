using Moq;
using Kintsugi.Application.AiSettings;
using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Application.UpgradePaths;
using Kintsugi.Application.UpgradePaths.Commands.ResearchApplicationUpgradePath;
using Kintsugi.Domain.Entities;
using Kintsugi.Domain.Enums;

namespace Kintsugi.Tests.Application.UpgradePaths;

public class ResearchProviderMisconfigurationTests
{
    private readonly Mock<IUpgradePathRepository> _repository = new();
    private readonly Mock<IUpgradePathResearchClient> _client = new();
    private readonly Mock<IArtifactSigningService> _signing = new();
    private readonly Mock<IUnitOfWork> _unitOfWork = new();

    private static readonly AiProviderSettings Settings = new(AiProvider.Anthropic, "sk", null, "m");

    public ResearchProviderMisconfigurationTests()
    {
        _signing.Setup(s => s.Sign(It.IsAny<string>())).Returns<string?>(c => c);
        _repository.Setup(r => r.GetAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync((UpgradePath?)null);
    }

    private static ResearchApplicationUpgradePathCommand Command(string name) =>
        new(name, PlatformBucket.MacOs, [], UpgradePathWorkKind.Research, null, null, Settings);

    [Fact]
    public async Task AMisconfiguredProvider_GivesEveryApplicationTheSameOneClearNote_NotARawHttpDump()
    {
        var error = new AiProviderMisconfiguredException(
            AiProviderMisconfigurationKind.ModelNotFound, "Vertex - Anthropic", "claude-haiku-5-5@default",
            "model 'claude-haiku-5-5@default' was not found on connection 'Vertex - Anthropic'.");
        _client.Setup(c => c.GenerateScriptAsync(It.IsAny<AiProviderSettings>(), It.IsAny<UpgradePathScriptGenerationRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(error);
        var handler = new ResearchApplicationUpgradePathCommandHandler(_repository.Object, _client.Object, _signing.Object, _unitOfWork.Object);

        var one = await handler.Handle(Command("Claude"), CancellationToken.None);
        var two = await handler.Handle(Command("PdfCraft"), CancellationToken.None);

        Assert.Equal(UpgradePathStatus.Failed, one.Status);
        Assert.Equal(error.Message, one.Note);
        Assert.Equal(error.Message, two.Note);
        Assert.DoesNotContain("Could not generate an upgrade script", one.Note);
        Assert.DoesNotContain("HTTP", one.Note);
    }

    [Fact]
    public async Task AMisconfiguredProvider_LeavesAScriptThatAlreadyExistsExactlyAsItWas()
    {
        var existing = UpgradePath.Create("Claude", PlatformBucket.MacOs, UpgradePathStatus.Found, "1.0", UpgradeMethod.Script, null, null, null, null, null, "#!/bin/bash\necho hi", null);
        _repository.Setup(r => r.GetAsync("Claude", PlatformBucket.MacOs, It.IsAny<CancellationToken>())).ReturnsAsync(existing);
        _client.Setup(c => c.GenerateScriptAsync(It.IsAny<AiProviderSettings>(), It.IsAny<UpgradePathScriptGenerationRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new AiProviderMisconfiguredException(AiProviderMisconfigurationKind.AuthFailed, "V", "m", "refused."));
        var handler = new ResearchApplicationUpgradePathCommandHandler(_repository.Object, _client.Object, _signing.Object, _unitOfWork.Object);

        var result = await handler.Handle(Command("Claude") with { ForceRecheck = true }, CancellationToken.None);

        Assert.Equal(UpgradePathStatus.Found, result.Status);
        Assert.Equal("#!/bin/bash\necho hi", existing.Script);
        Assert.Contains("AI provider misconfigured: refused.", result.Note);
        Assert.Contains("kept unchanged", result.Note);
    }
}
