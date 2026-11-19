using MediatR;
using Moq;
using Kintsugi.Application.Common.Exceptions;
using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Application.ForcedPatchRuns;
using Kintsugi.Application.ForcedPatchRuns.Commands.RequestForcedPatchRuns;
using Kintsugi.Application.SoftwareTitles;
using Kintsugi.Application.SoftwareTitles.Commands.AssignSoftwareTitle;
using Kintsugi.Application.SoftwareTitles.Commands.CreateSoftwareTitle;
using Kintsugi.Application.SoftwareTitles.Commands.DeleteScriptSecret;
using Kintsugi.Application.SoftwareTitles.Commands.DeleteSoftwareTitle;
using Kintsugi.Application.SoftwareTitles.Commands.RunSoftwareTitleNow;
using Kintsugi.Application.SoftwareTitles.Commands.SetScriptSecret;
using Kintsugi.Application.SoftwareTitles.Commands.UpdateSoftwareTitle;
using Kintsugi.Application.SoftwareTitles.Queries.GetSoftware;
using Kintsugi.Application.UpgradePaths;
using Kintsugi.Domain.Entities;
using Kintsugi.Domain.Enums;
using Kintsugi.Domain.Exceptions;

namespace Kintsugi.Tests.Application.SoftwareTitles;

/// <summary>
/// The Software screen's commands and query: creating (and seeding a manager's row), signing on save
/// only, assignment's all-or-nothing validation, the counts, Run now, and the write-only secrets.
/// </summary>
public class SoftwareTitleCommandTests
{
    private readonly SoftwareTestbed _bed = new();
    private readonly Mock<IArtifactSigningService> _signing = new();
    private readonly Mock<ISender> _sender = new();
    private readonly List<RequestForcedPatchRunsCommand> _forced = new();

    public SoftwareTitleCommandTests()
    {
        _signing.Setup(s => s.Sign(It.IsAny<string?>())).Returns<string?>(text => "signed:" + text);
        _sender.Setup(s => s.Send(It.IsAny<GetSoftwareQuery>(), It.IsAny<CancellationToken>()))
            .Returns<GetSoftwareQuery, CancellationToken>((q, ct) => Query().Handle(q, ct));
        _sender.Setup(s => s.Send(It.IsAny<RequestForcedPatchRunsCommand>(), It.IsAny<CancellationToken>()))
            .Callback<IRequest<RequestForcedPatchRunsResult>, CancellationToken>((c, _) => _forced.Add((RequestForcedPatchRunsCommand)c))
            .ReturnsAsync((IRequest<RequestForcedPatchRunsResult> c, CancellationToken _) =>
                new RequestForcedPatchRunsResult(((RequestForcedPatchRunsCommand)c).HostNames.Count, Array.Empty<string>()));
    }

    private GetSoftwareQueryHandler Query() =>
        new(_bed.Titles, _bed.Secrets, _bed.Hosts, _bed.Installed, _bed.Failures, _bed.UpgradePaths);

    private Task<SoftwareScreenDto> Create(
        string name, string? manager, string? platform, string? packageId, SoftwareMode mode = SoftwareMode.Required, params string[] secrets) =>
        new CreateSoftwareTitleCommandHandler(_bed.Titles, _bed.Secrets, _bed.UpgradePaths, _signing.Object, _bed.Context, _sender.Object)
            .Handle(new CreateSoftwareTitleCommand(name, manager, platform, packageId, null, mode, secrets, "admin@example.com"), CancellationToken.None);

    // ---- Create ----

    [Fact]
    public async Task Create_APackageManagerTitle_SignsItsCanonicalText_AndSeedsAnUnsignedRowInTheManagersBucket()
    {
        var screen = await Create("Greenshot", "WinGet", null, "Greenshot.Greenshot");

        var title = _bed.Context.SoftwareTitles.Single();
        Assert.Equal("winget", title.PackageManager);
        Assert.Equal("pm:winget", title.Platform);
        Assert.Equal("Windows", title.OperatingSystem);
        Assert.Equal("signed:" + title.CanonicalActionText(), title.ActionSignature);
        Assert.Equal("admin@example.com", title.CreatedBy);

        // Upgrade Scripts now lists the manager's script to Take + Sign, so Missing has a remedy.
        var seeded = _bed.Context.UpgradePaths.Single();
        Assert.Equal(("Greenshot", "pm:winget", "Greenshot.Greenshot"), (seeded.ApplicationName, seeded.Platform, seeded.ApplicationIdentifier));
        Assert.Equal(WingetUpgradeScript.Build(), seeded.Script);
        Assert.Null(seeded.ScriptSignature);

        var dto = Assert.Single(screen.Titles);
        Assert.Equal(("Windows", "winget", SoftwareScriptState.Unsigned), (dto.Platform, dto.PackageManager, dto.ScriptState));
    }

    [Fact]
    public async Task Create_APackageManagerTitle_JoinsTheBucketsReviewedScript_WhenThereIsOne()
    {
        _bed.Script("winget", "pm:winget", WingetUpgradeScript.Build(), signed: true);

        var screen = await Create("Greenshot", "winget", null, "Greenshot.Greenshot");

        var seeded = _bed.Context.UpgradePaths.Single(p => p.ApplicationName == "Greenshot");
        Assert.Equal("script-sig", seeded.ScriptSignature);
        Assert.Equal(SoftwareScriptState.Ready, Assert.Single(screen.Titles).ScriptState);
        // The signing service was asked to sign the action text and nothing else.
        _signing.Verify(s => s.Sign(It.Is<string?>(t => t!.StartsWith("kintsugi-action-v1\n"))), Times.Once);
        _signing.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Create_DoesNotSeedARow_WhenThePackageAlreadyHasOne()
    {
        _bed.Script("Greenshot", "pm:winget", "existing", signed: false);

        await Create("Greenshot", "winget", null, "Greenshot.Greenshot");

        Assert.Equal("existing", _bed.Context.UpgradePaths.Single().Script);
    }

    [Fact]
    public async Task Create_ACustomTitle_SeedsNothing_AndIsMissingUntilResearched()
    {
        var screen = await Create("Zoho Assist", null, "windows", null);

        Assert.Empty(_bed.Context.UpgradePaths);
        var dto = Assert.Single(screen.Titles);
        Assert.Equal(("Windows", null, SoftwareScriptState.Missing), (dto.Platform, dto.PackageManager, dto.ScriptState));
    }

    [Fact]
    public async Task Create_RefusesTheAppStore_BecauseMasInstallNeedsTheTrackId()
    {
        var ex = await Assert.ThrowsAsync<DomainException>(() => Create("Xcode", "App Store", null, "com.apple.dt.Xcode"));
        Assert.Contains("mas install", ex.Message);
    }

    [Fact]
    public async Task Create_RefusesAnUnknownManager_AndAnUnknownPlatform()
    {
        await Assert.ThrowsAsync<DomainException>(() => Create("x", "apt", null, "x"));
        await Assert.ThrowsAsync<DomainException>(() => Create("x", null, "BeOS", null));
    }

    [Fact]
    public async Task Create_RefusesASecretThatIsNotStored()
    {
        await Assert.ThrowsAsync<DomainException>(() => Create("Zoho Assist", null, "Windows", null, SoftwareMode.Required, "TEST_SECRET_A"));
        Assert.Empty(_bed.Context.SoftwareTitles);
    }

    [Fact]
    public async Task Create_RefusesADuplicate_IgnoringCase()
    {
        await Create("Zoho Assist", null, "Windows", null);

        await Assert.ThrowsAsync<ConflictException>(() => Create("zoho assist", null, "Windows", null));
    }

    // ---- Update ----

    [Fact]
    public async Task Update_ReSigns_WhenTheModeTurnsAnInstallIntoAnUninstall()
    {
        _bed.Secret("TEST_SECRET_A");
        await Create("Zoho Assist", null, "Windows", null);
        var title = _bed.Context.SoftwareTitles.Single();
        var before = title.ActionSignature;

        await new UpdateSoftwareTitleCommandHandler(_bed.Titles, _bed.Secrets, _signing.Object, _bed.Context, _sender.Object)
            .Handle(new UpdateSoftwareTitleCommand(title.Id, SoftwareMode.Prohibited, "Zoho", ["TEST_SECRET_A"]), CancellationToken.None);

        Assert.NotEqual(before, title.ActionSignature);
        Assert.Equal("signed:" + title.CanonicalActionText(), title.ActionSignature);
        Assert.Contains("\nUninstall\n", title.ActionSignature);
        Assert.Equal("Zoho", title.DetectAs);
    }

    // ---- Assign ----

    [Fact]
    public async Task Assign_IsAllOrNothing_AndRefusesAHostOfAnotherOs()
    {
        var mac = new Host("mac-1", "SERIAL-2", "macOS 15.1");
        _bed.Context.Hosts.Add(mac);
        _bed.Context.SaveChanges();
        await Create("Zoho Assist", null, "Windows", null);
        var title = _bed.Context.SoftwareTitles.Single();
        var handler = new AssignSoftwareTitleCommandHandler(_bed.Titles, _bed.Hosts, _bed.Context, _sender.Object);

        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            handler.Handle(new AssignSoftwareTitleCommand(title.Id, false, ["pc-1", "mac-1", "nope"], "admin@example.com"), CancellationToken.None));
        Assert.Contains("mac-1", ex.Message);
        Assert.Contains("nope", ex.Message);
        Assert.Empty(title.AssignedHostIds);

        var screen = await handler.Handle(new AssignSoftwareTitleCommand(title.Id, false, ["pc-1"], "admin@example.com"), CancellationToken.None);
        Assert.Equal([_bed.Host.Id], title.AssignedHostIds);
        Assert.Equal("admin@example.com", title.AssignedBy);
        Assert.Equal(["pc-1"], Assert.Single(screen.Titles).AssignedHostNames);
    }

    // ---- Counts ----

    [Fact]
    public async Task GetSoftware_CountsCompliantPendingFailedAndNotApplicable()
    {
        Host AddHost(string name)
        {
            var host = new Host(name, "SERIAL-" + name, "Windows 11 Pro");
            _bed.Context.Hosts.Add(host);
            _bed.Context.SaveChanges();
            return host;
        }

        var compliant = AddHost("compliant");
        var failed = AddHost("failed");
        AddHost("no-winget");
        _bed.Install("winget");                              // pc-1: pending
        _bed.Install("winget", host: compliant);
        _bed.Install("Greenshot", "Greenshot.Greenshot", compliant);
        _bed.Install("winget", host: failed);
        _bed.Context.PatchFailures.Add(PatchFailure.Open(failed.Id, "greenshot", null, "", null, "exit 1", DateTimeOffset.UtcNow));
        _bed.Context.SaveChanges();
        var title = SoftwareTitle.Create("Greenshot", "pm:winget", "winget", "Windows", "Greenshot.Greenshot", null, SoftwareMode.Required, [], "admin");
        title.Assign(true, [], "admin");
        _bed.AddTitle(title, assign: false);

        var dto = Assert.Single((await Query().Handle(new GetSoftwareQuery(), CancellationToken.None)).Titles);

        Assert.Equal((1, 1, 1, 1), (dto.CompliantCount, dto.PendingCount, dto.FailedCount, dto.NotApplicableCount));
        Assert.True(dto.AssignedToAll);
    }

    [Fact]
    public async Task GetSoftware_AProhibitedTitle_IsCompliantWhereAbsent()
    {
        _bed.CustomTitle(SoftwareMode.Prohibited);

        var dto = Assert.Single((await Query().Handle(new GetSoftwareQuery(), CancellationToken.None)).Titles);

        Assert.Equal((1, 0), (dto.CompliantCount, dto.PendingCount));
    }

    // ---- Run now ----

    [Fact]
    public async Task RunNow_RequestsForcedRuns_ForPendingAndFailedHostsOnly()
    {
        _bed.Script("Zoho Assist", "Windows", SoftwareTestbed.ActionScript, signed: true);
        var done = new Host("done", "SERIAL-3", "Windows 11");
        _bed.Context.Hosts.Add(done);
        _bed.Context.SaveChanges();
        _bed.Install("Zoho Assist", host: done);
        var title = SoftwareTitle.Create("Zoho Assist", "Windows", null, "Windows", null, null, SoftwareMode.Required, [], "admin");
        title.Assign(true, [], "admin");
        _bed.AddTitle(title, assign: false);

        var result = await new RunSoftwareTitleNowCommandHandler(_bed.Titles, _bed.Hosts, _bed.Installed, _bed.Failures, _bed.UpgradePaths, _sender.Object)
            .Handle(new RunSoftwareTitleNowCommand(title.Id, "admin@example.com"), CancellationToken.None);

        var forced = Assert.Single(_forced);
        Assert.Equal(("Zoho Assist", "Windows", "admin@example.com"), (forced.ApplicationName, forced.Platform, forced.RequestedBy));
        Assert.Equal(["pc-1"], forced.HostNames);
        Assert.Equal(1, result.Requested);
    }

    [Fact]
    public async Task RunNow_RefusesSelfService_AndAScriptThatIsNotReady()
    {
        var offered = _bed.CustomTitle(SoftwareMode.SelfService);
        var handler = new RunSoftwareTitleNowCommandHandler(_bed.Titles, _bed.Hosts, _bed.Installed, _bed.Failures, _bed.UpgradePaths, _sender.Object);

        await Assert.ThrowsAsync<DomainException>(() => handler.Handle(new RunSoftwareTitleNowCommand(offered.Id), CancellationToken.None));

        var required = _bed.CustomTitle(SoftwareMode.Required, name: "Other");
        await Assert.ThrowsAsync<DomainException>(() => handler.Handle(new RunSoftwareTitleNowCommand(required.Id), CancellationToken.None));
        Assert.Empty(_forced);
    }

    // ---- Delete ----

    [Fact]
    public async Task Delete_RemovesTheTitle_AndKeepsItsUpgradePath()
    {
        _bed.Script("Zoho Assist", "Windows", SoftwareTestbed.ActionScript, signed: true);
        var title = _bed.CustomTitle(SoftwareMode.Required);

        var screen = await new DeleteSoftwareTitleCommandHandler(_bed.Titles, _bed.Context, _sender.Object)
            .Handle(new DeleteSoftwareTitleCommand(title.Id), CancellationToken.None);

        Assert.Empty(screen.Titles);
        Assert.Single(_bed.Context.UpgradePaths);
    }

    // ---- Secrets ----

    [Fact]
    public async Task SetSecret_SignsTheCanonicalText_AndTheScreenNeverReturnsTheValue()
    {
        var screen = await new SetScriptSecretCommandHandler(_bed.Secrets, _signing.Object, _bed.Context, _sender.Object)
            .Handle(new SetScriptSecretCommand("TEST_SECRET_A", "TEST_SECRET_VALUE", "admin@example.com"), CancellationToken.None);

        var stored = _bed.Context.ScriptSecrets.Single();
        Assert.Equal("signed:kintsugi-secret-v1\nTEST_SECRET_A\nTEST_SECRET_VALUE", stored.ValueSignature);
        var summary = Assert.Single(screen.Secrets);
        Assert.Equal(("TEST_SECRET_A", true, "admin@example.com"), (summary.Name, summary.HasValue, summary.UpdatedBy));

        var json = System.Text.Json.JsonSerializer.Serialize(screen);
        Assert.DoesNotContain("TEST_SECRET_VALUE", json);
    }

    [Fact]
    public async Task SetSecret_ReplacesAValue_AndReSignsIt()
    {
        var handler = new SetScriptSecretCommandHandler(_bed.Secrets, _signing.Object, _bed.Context, _sender.Object);
        await handler.Handle(new SetScriptSecretCommand("TEST_SECRET_A", "TEST_SECRET_VALUE", "a"), CancellationToken.None);

        await handler.Handle(new SetScriptSecretCommand("TEST_SECRET_A", "TEST_SECRET_VALUE_2", "b"), CancellationToken.None);

        var stored = _bed.Context.ScriptSecrets.Single();
        Assert.Equal("TEST_SECRET_VALUE_2", stored.Value);
        Assert.EndsWith("\nTEST_SECRET_VALUE_2", stored.ValueSignature);
    }

    [Fact]
    public void SecretSummaryDto_HasNoValueProperty_AndCommandsDoNotPrintTheValue()
    {
        Assert.DoesNotContain(typeof(ScriptSecretSummaryDto).GetProperties(), p => p.Name.Contains("Value", StringComparison.Ordinal) && p.Name != "HasValue");
        Assert.DoesNotContain("TEST_SECRET_VALUE", new SetScriptSecretCommand("TEST_SECRET_A", "TEST_SECRET_VALUE").ToString());
        Assert.DoesNotContain("TEST_SECRET_VALUE", new ScriptSecretDto("TEST_SECRET_A", "TEST_SECRET_VALUE", "sig").ToString());
    }

    [Fact]
    public async Task DeleteSecret_IsRefusedWhileATitleUsesIt()
    {
        _bed.Secret("TEST_SECRET_A");
        _bed.CustomTitle(SoftwareMode.Required, secrets: "TEST_SECRET_A");
        var handler = new DeleteScriptSecretCommandHandler(_bed.Secrets, _bed.Titles, _bed.Context, _sender.Object);

        await Assert.ThrowsAsync<ConflictException>(() => handler.Handle(new DeleteScriptSecretCommand("TEST_SECRET_A"), CancellationToken.None));
        Assert.Single(_bed.Context.ScriptSecrets);
    }

    [Fact]
    public async Task DeleteSecret_RemovesOneNoTitleUses()
    {
        _bed.Secret("TEST_SECRET_A");

        var screen = await new DeleteScriptSecretCommandHandler(_bed.Secrets, _bed.Titles, _bed.Context, _sender.Object)
            .Handle(new DeleteScriptSecretCommand("TEST_SECRET_A"), CancellationToken.None);

        Assert.Empty(screen.Secrets);
        await Assert.ThrowsAsync<NotFoundException>(() => new DeleteScriptSecretCommandHandler(_bed.Secrets, _bed.Titles, _bed.Context, _sender.Object)
            .Handle(new DeleteScriptSecretCommand("TEST_SECRET_A"), CancellationToken.None));
    }

    [Fact]
    public void Validators_RejectBadSecretNamesAndAnEmptyValue()
    {
        Assert.False(new SetScriptSecretCommandValidator().Validate(new SetScriptSecretCommand("lower", "v")).IsValid);
        Assert.False(new SetScriptSecretCommandValidator().Validate(new SetScriptSecretCommand("TEST_SECRET_A", "")).IsValid);
        Assert.True(new SetScriptSecretCommandValidator().Validate(new SetScriptSecretCommand("TEST_SECRET_A", "v")).IsValid);
        Assert.False(new CreateSoftwareTitleCommandValidator().Validate(
            new CreateSoftwareTitleCommand("x", "winget", null, null, null, SoftwareMode.Required, [])).IsValid);
        Assert.False(new CreateSoftwareTitleCommandValidator().Validate(
            new CreateSoftwareTitleCommand("x", null, null, null, null, SoftwareMode.Required, [])).IsValid);
        Assert.False(new CreateSoftwareTitleCommandValidator().Validate(
            new CreateSoftwareTitleCommand("x", null, "Windows", null, null, SoftwareMode.Required, ["bad-name"])).IsValid);
    }
}
