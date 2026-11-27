using MediatR;
using Moq;
using Kintsugi.Application.Common.Exceptions;
using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Application.PatchFailures;
using Kintsugi.Application.PatchFailures.Commands.DraftPatchFailureRepairs;
using Kintsugi.Application.UpgradePaths;
using Kintsugi.Application.UpgradePaths.Commands.RefreshUpgradePath;
using Kintsugi.Application.UpgradePaths.Queries.GetUpgradePathPrompt;
using Kintsugi.Domain.Entities;
using Kintsugi.Domain.Enums;

namespace Kintsugi.Tests.Application.PatchFailures;

public class DraftPatchFailureRepairsCommandHandlerTests
{
    private static readonly DateTimeOffset Earlier = DateTimeOffset.UtcNow.AddDays(-3);
    private readonly Mock<IPatchFailureRepository> _failures = new();
    private readonly Mock<IUpgradePathRepository> _paths = new();
    private readonly Mock<IAiBudgetService> _budget = new();
    private readonly Mock<ISender> _sender = new();
    private readonly RepairDraftAttempts _attempts = new();
    private readonly List<PatchFailure> _outstanding = [];
    private readonly Dictionary<string, UpgradePath> _pathsByName = new(StringComparer.OrdinalIgnoreCase);

    public DraftPatchFailureRepairsCommandHandlerTests()
    {
        _failures.Setup(f => f.GetAllOutstandingAsync(It.IsAny<CancellationToken>())).ReturnsAsync(() => _outstanding);
        _paths.Setup(p => p.GetAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string name, string platform, CancellationToken _) =>
                _pathsByName.TryGetValue(name, out var path) && path.Platform == platform ? path : null);
        _sender.Setup(s => s.Send(It.IsAny<GetUpgradePathPromptQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((GetUpgradePathPromptQuery q, CancellationToken _) => new UpgradePathPromptDto(true, q.Platform, $"prompt for {q.PatchFailureId}", null));
        // The AI answers with a draft, which the research handler stores as the candidate.
        _sender.Setup(s => s.Send(It.IsAny<RefreshUpgradePathCommand>(), It.IsAny<CancellationToken>()))
            .Callback((IRequest<RefreshUpgradePathResult> r, CancellationToken _) =>
            {
                var command = (RefreshUpgradePathCommand)r;
                _pathsByName[command.ApplicationName].ProposeRepair("repaired", "notes", command.RepairDraftFor!.Value, DateTimeOffset.UtcNow);
            })
            .ReturnsAsync(new RefreshUpgradePathResult(true, null));
    }

    private DraftPatchFailureRepairsCommandHandler Create() => new(_failures.Object, _paths.Object, _budget.Object, _attempts, _sender.Object);

    private UpgradePath Path(string name, string platform = PlatformBucket.Windows)
    {
        var path = UpgradePath.Create(name, platform, UpgradePathStatus.Found, "1.0", UpgradeMethod.Script, null, null, null, null, null, "signed script");
        path.SignScript("signature");
        _pathsByName[name] = path;
        return path;
    }

    private PatchFailure Failure(
        string name, string platform = PlatformBucket.Windows, Guid? host = null, int times = 1,
        string category = FailureClassifier.ScriptError, DateTimeOffset? firstFailed = null)
    {
        var failure = PatchFailure.Open(host ?? Guid.NewGuid(), name, platform, "1.0", "2.0", "The property 'Properties' cannot be found", firstFailed ?? Earlier);
        for (var i = 1; i < times; i++)
        {
            failure.RecordAnotherFailure(platform, "1.0", "2.0", "The property 'Properties' cannot be found", (firstFailed ?? Earlier).AddHours(i));
        }
        failure.SetCategory(category);
        _outstanding.Add(failure);
        return failure;
    }

    private void VerifyRefreshed(string name, Times times) =>
        _sender.Verify(s => s.Send(It.Is<RefreshUpgradePathCommand>(c => c.ApplicationName == name), It.IsAny<CancellationToken>()), times);

    [Fact]
    public async Task AScriptThatFailedTwice_GetsADraft_FromTheRepairPrompt()
    {
        var path = Path("Docker Desktop");
        var failure = Failure("Docker Desktop", times: 2);

        var result = await Create().Handle(new DraftPatchFailureRepairsCommand(), CancellationToken.None);

        Assert.Equal(1, result.Drafted);
        _sender.Verify(s => s.Send(
            It.Is<RefreshUpgradePathCommand>(c => c.RepairDraftFor == failure.Id && c.PromptOverride == $"prompt for {failure.Id}" && c.Platform == PlatformBucket.Windows),
            It.IsAny<CancellationToken>()), Times.Once);
        Assert.Equal("signed script", path.Script);
        Assert.Equal("signature", path.ScriptSignature);
    }

    [Fact]
    public async Task OneFailureOnEachOfTwoHosts_IsEnough()
    {
        Path("DBeaver");
        Failure("DBeaver");
        Failure("DBeaver");

        Assert.Equal(1, (await Create().Handle(new DraftPatchFailureRepairsCommand(), CancellationToken.None)).Drafted);
    }

    [Fact]
    public async Task OneFailureOnOneHost_IsNotEnough()
    {
        Path("DBeaver");
        Failure("DBeaver");

        await Create().Handle(new DraftPatchFailureRepairsCommand(), CancellationToken.None);

        VerifyRefreshed("DBeaver", Times.Never());
    }

    [Theory]
    [InlineData(FailureClassifier.NetworkOrCertificate)]
    [InlineData(FailureClassifier.FileInUse)]
    [InlineData(FailureClassifier.DiskSpace)]
    public async Task ATransientFailure_GetsNoDraft(string category)
    {
        Path("Zoom");
        Failure("Zoom", times: 5, category: category);

        await Create().Handle(new DraftPatchFailureRepairsCommand(), CancellationToken.None);

        VerifyRefreshed("Zoom", Times.Never());
    }

    [Theory]
    [InlineData("Firefox", PlatformBucket.Linux)]
    [InlineData("Firefox", "pm:winget")]
    [InlineData("Windows", PlatformBucket.Windows)]
    public async Task OnlyAiWrittenApplicationScriptsGetADraft(string name, string platform)
    {
        Path(name, platform);
        Failure(name, platform, times: 5);

        await Create().Handle(new DraftPatchFailureRepairsCommand(), CancellationToken.None);

        VerifyRefreshed(name, Times.Never());
    }

    [Fact]
    public async Task ADraftNewerThanEveryFailure_IsLeftAlone_EvenThoughTheScriptKeepsFailing()
    {
        var path = Path("Acrobat");
        var failure = Failure("Acrobat", times: 2);
        path.ProposeRepair("draft", null, failure.Id, Earlier.AddDays(1));
        // The signed script runs again and fails again: LastFailedUtc moves past the draft, FirstFailedUtc does not.
        failure.RecordAnotherFailure(PlatformBucket.Windows, "1.0", "2.0", "still failing", DateTimeOffset.UtcNow);

        await Create().Handle(new DraftPatchFailureRepairsCommand(), CancellationToken.None);

        VerifyRefreshed("Acrobat", Times.Never());
        Assert.Equal("draft", path.CandidateScript);
    }

    [Fact]
    public async Task AFailureThatFirstHappenedAfterTheDraft_GetsAFreshOne()
    {
        var path = Path("Acrobat");
        var first = Failure("Acrobat", times: 2);
        path.ProposeRepair("draft", null, first.Id, Earlier.AddDays(1));
        Failure("Acrobat", firstFailed: Earlier.AddDays(2));

        await Create().Handle(new DraftPatchFailureRepairsCommand(), CancellationToken.None);

        Assert.Equal("repaired", path.CandidateScript);
    }

    [Fact]
    public async Task AtMostThreePathsArePutToTheAiInOnePass()
    {
        foreach (var name in new[] { "A", "B", "C", "D", "E" })
        {
            Path(name);
            Failure(name, times: 2);
        }

        var result = await Create().Handle(new DraftPatchFailureRepairsCommand(), CancellationToken.None);

        Assert.Equal(DraftPatchFailureRepairsCommand.MaxPerPass, result.Drafted);
        _sender.Verify(s => s.Send(It.IsAny<RefreshUpgradePathCommand>(), It.IsAny<CancellationToken>()), Times.Exactly(3));
    }

    [Fact]
    public async Task AtTheBudgetLimit_ThePassStopsWithoutCallingTheAi()
    {
        Path("Docker Desktop");
        Failure("Docker Desktop", times: 2);
        _budget.Setup(b => b.EnsureWithinBudgetAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new AiBudgetReachedException("This month's AI budget has been reached."));

        var result = await Create().Handle(new DraftPatchFailureRepairsCommand(), CancellationToken.None);

        Assert.Equal(0, result.Drafted);
        Assert.Contains("budget", Assert.Single(result.Notes));
        _sender.Verify(s => s.Send(It.IsAny<GetUpgradePathPromptQuery>(), It.IsAny<CancellationToken>()), Times.Never);
        VerifyRefreshed("Docker Desktop", Times.Never());
    }

    [Fact]
    public async Task APathWithNoPrompt_IsNotSentToTheAi_AndNotTriedAgainToday()
    {
        Path("CrowdStrike Falcon");
        Failure("CrowdStrike Falcon", times: 2);
        _sender.Setup(s => s.Send(It.IsAny<GetUpgradePathPromptQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UpgradePathPromptDto(false, PlatformBucket.Windows, null, "Managed by CrowdStrike"));

        var first = await Create().Handle(new DraftPatchFailureRepairsCommand(), CancellationToken.None);
        await Create().Handle(new DraftPatchFailureRepairsCommand(), CancellationToken.None);

        Assert.Contains("Managed by CrowdStrike", Assert.Single(first.Notes));
        VerifyRefreshed("CrowdStrike Falcon", Times.Never());
        _sender.Verify(s => s.Send(It.IsAny<GetUpgradePathPromptQuery>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task APathTheAiCouldNotRepair_IsNotRetriedTheSameDay()
    {
        Path("Docker Desktop");
        Failure("Docker Desktop", times: 2);
        _sender.Setup(s => s.Send(It.IsAny<RefreshUpgradePathCommand>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RefreshUpgradePathResult(true, null));

        await Create().Handle(new DraftPatchFailureRepairsCommand(), CancellationToken.None);
        await Create().Handle(new DraftPatchFailureRepairsCommand(), CancellationToken.None);

        VerifyRefreshed("Docker Desktop", Times.Once());
    }
}
