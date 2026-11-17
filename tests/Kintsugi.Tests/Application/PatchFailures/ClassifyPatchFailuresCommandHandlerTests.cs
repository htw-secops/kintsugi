using Moq;
using Kintsugi.Application.AiSettings;
using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Application.PatchFailures;
using Kintsugi.Application.PatchFailures.Commands.ClassifyPatchFailures;
using Kintsugi.Domain.Entities;
using Kintsugi.Domain.Enums;

namespace Kintsugi.Tests.Application.PatchFailures;

public class ClassifyPatchFailuresCommandHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 17, 5, 0, 0, TimeSpan.Zero);
    private readonly Mock<IPatchFailureRepository> _failures = new();
    private readonly Mock<IHostRepository> _hosts = new();
    private readonly Mock<IUnitOfWork> _unitOfWork = new();
    private readonly Mock<IAiProviderSettingsResolver> _settings = new();
    private readonly Mock<IFailureClassificationClient> _client = new();
    private readonly FailureClassificationCache _cache = new();
    private readonly Host _host = new("host-1", "SERIAL-1", "Windows 11");

    private ClassifyPatchFailuresCommandHandler Create() =>
        new(_failures.Object, _hosts.Object, new AiFailureClassifier(_settings.Object, _client.Object, _cache), _unitOfWork.Object);

    private PatchFailure Unclassified(string details = "weird output nothing matches")
    {
        var f = PatchFailure.Open(_host.Id, "Zoom", "winget", "1.0", "2.0", details, Now.AddHours(-1));
        f.SetCategory(FailureClassifier.ScriptError);
        f.SetSignature(FailureText.Signature("Zoom", FailureClassifier.ScriptError, details));
        return f;
    }

    private void Configured(string? answer)
    {
        _hosts.Setup(h => h.GetByIdAsync(_host.Id, It.IsAny<CancellationToken>())).ReturnsAsync(_host);
        _settings.Setup(s => s.ResolveAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AiProviderSettings(AiProvider.Routed, null, null, null));
        _client.Setup(c => c.ClassifyFailureAsync(It.IsAny<AiProviderSettings>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(answer!);
    }

    [Fact]
    public async Task ANamedFailure_GetsTheNewCategory_AndASignatureWorkedOutAgain()
    {
        var failure = Unclassified();
        var before = failure.FailureSignature;
        _failures.Setup(r => r.GetAllOutstandingAsync(It.IsAny<CancellationToken>())).ReturnsAsync([failure]);
        Configured("DiskSpace");

        var named = await Create().Handle(new ClassifyPatchFailuresCommand(), CancellationToken.None);

        Assert.Equal(1, named);
        Assert.Equal("DiskSpace", failure.FailureCategory);
        Assert.NotEqual(before, failure.FailureSignature);
        Assert.Equal(FailureText.Signature("Zoom", "DiskSpace", failure.Details), failure.FailureSignature);
        _unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task OnlyFailuresTheRulesCouldNotName_AreEverSent()
    {
        var named = PatchFailure.Open(_host.Id, "Slack", "winget", "1.0", "2.0", "Access is denied", Now.AddHours(-1));
        named.SetCategory("PermissionDenied");
        _failures.Setup(r => r.GetAllOutstandingAsync(It.IsAny<CancellationToken>())).ReturnsAsync([named]);
        Configured("DiskSpace");

        var count = await Create().Handle(new ClassifyPatchFailuresCommand(), CancellationToken.None);

        Assert.Equal(0, count);
        Assert.Equal("PermissionDenied", named.FailureCategory);
        _client.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task AnAnswerThatDoesNotStick_IsNotSentAgainTheSameDay_SoItCannotSpendTheWholeCap()
    {
        var failure = Unclassified();
        _failures.Setup(r => r.GetAllOutstandingAsync(It.IsAny<CancellationToken>())).ReturnsAsync([failure]);
        Configured("Unknown");
        var sut = Create();

        await sut.Handle(new ClassifyPatchFailuresCommand(), CancellationToken.None);
        await sut.Handle(new ClassifyPatchFailuresCommand(), CancellationToken.None);

        Assert.Equal("ScriptError", failure.FailureCategory);
        _client.Verify(c => c.ClassifyFailureAsync(It.IsAny<AiProviderSettings>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task NothingIsSaved_WhenNothingWasNamed()
    {
        var failure = Unclassified();
        _failures.Setup(r => r.GetAllOutstandingAsync(It.IsAny<CancellationToken>())).ReturnsAsync([failure]);
        _hosts.Setup(h => h.GetByIdAsync(_host.Id, It.IsAny<CancellationToken>())).ReturnsAsync(_host);
        _settings.Setup(s => s.ResolveAsync(It.IsAny<CancellationToken>())).ReturnsAsync((AiProviderSettings?)null);

        var named = await Create().Handle(new ClassifyPatchFailuresCommand(), CancellationToken.None);

        Assert.Equal(0, named);
        _unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task NoMoreThanTheCapPerPassAreSent()
    {
        var many = Enumerable.Range(0, 25).Select(i => Unclassified($"odd output number {i} letters{(char)('a' + i)}")).ToList();
        _failures.Setup(r => r.GetAllOutstandingAsync(It.IsAny<CancellationToken>())).ReturnsAsync(many);
        Configured("DiskSpace");

        var named = await Create().Handle(new ClassifyPatchFailuresCommand(), CancellationToken.None);

        Assert.True(named <= ClassifyPatchFailuresCommand.MaxPerPass);
    }
}
