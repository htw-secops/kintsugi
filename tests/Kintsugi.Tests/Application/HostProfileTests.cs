using Moq;
using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Application.DeviceHealth;
using Kintsugi.Application.HostActivity;
using Kintsugi.Application.Hosts;
using Kintsugi.Application.Hosts.Commands.ImportHostProfiles;
using Kintsugi.Application.Hosts.Commands.RevealAssignedPerson;
using Kintsugi.Application.Hosts.Commands.UpdateHostProfile;
using Kintsugi.Application.Hosts.Queries.GetHosts;
using Kintsugi.Application.PatchFailures;
using Kintsugi.Application.Vulnerabilities;
using Kintsugi.Application.UpgradePaths;
using Kintsugi.Domain.Entities;
using Kintsugi.Domain.Enums;
using Kintsugi.Domain.Exceptions;
using Kintsugi.Application.Common.Exceptions;

namespace Kintsugi.Tests.Application;

public class HostProfileEntityTests
{
    [Fact]
    public void NullKeepsABlankClearsAndTextReplaces_AndOnlyRealChangesAreReported()
    {
        var host = new Host("pc", "S1");

        var first = host.SetProfile(department: "  Technology ", location: "Adelaide");
        Assert.Equal(["Department", "Location"], first);
        Assert.Equal("Technology", host.Department);   // trimmed

        var keep = host.SetProfile(department: null, location: "Adelaide");
        Assert.Empty(keep);                              // null keeps, and the same value is not a change
        Assert.Equal("Technology", host.Department);

        var clear = host.SetProfile(department: "   ");
        Assert.Equal(["Department"], clear);
        Assert.Null(host.Department);
        Assert.Equal("Adelaide", host.Location);
    }

    [Fact]
    public void ATextLongerThanItsColumnIsRefusedRatherThanCut()
    {
        var host = new Host("pc", "S1");

        Assert.Throws<DomainException>(() => host.SetProfile(department: new string('x', Host.MaxProfileTextLength + 1)));
        Assert.Throws<DomainException>(() => host.SetProfile(assignedEmail: new string('x', Host.MaxAssignedEmailLength + 1)));
        Assert.Null(host.Department);
    }
}

public class AssignedPersonTests
{
    [Theory]
    [InlineData("Alex Morgan", "Alex M.")]
    [InlineData("  alex   morgan  ", "alex M.")]
    [InlineData("Mary Jane Watson", "Mary W.")]
    [InlineData("Cher", "Cher")]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData(null, null)]
    public void OnlyTheFirstNameAndTheInitialOfTheLastLeaveTheServer(string? full, string? expected) =>
        Assert.Equal(expected, AssignedPerson.ShortName(full));
}

public class HostStandingTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 16, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void AFailedPatchOrAnUnstableDeviceIsAProblem_WithItsReasons()
    {
        Assert.Equal(("Problem", "Unstable, 2 failed patches"), HostStanding.Of(true, 2, AgentUpdateState.Current, Now, Now));
        Assert.Equal(("Problem", "1 failed patch"), HostStanding.Of(false, 1, AgentUpdateState.Current, Now, Now));
        Assert.Equal(("Problem", "Unstable"), HostStanding.Of(true, 0, AgentUpdateState.Current, Now, Now));
    }

    [Fact]
    public void ABehindAgentOrALongSilenceIsPending_AndAProblemOutranksIt()
    {
        Assert.Equal(("Pending", "Agent behind"), HostStanding.Of(false, 0, AgentUpdateState.Behind, Now, Now));
        Assert.Equal(("Pending", "Agent not updating"), HostStanding.Of(false, 0, AgentUpdateState.NotUpdating, Now, Now));
        Assert.Equal(("Pending", "Not seen for 3 days"), HostStanding.Of(false, 0, AgentUpdateState.Current, Now.AddDays(-3), Now));
        Assert.Equal(("Pending", "Never seen"), HostStanding.Of(false, 0, AgentUpdateState.Current, null, Now));
        Assert.Equal(("Problem", "1 failed patch"), HostStanding.Of(false, 1, AgentUpdateState.Behind, Now.AddDays(-9), Now));
    }

    [Fact]
    public void AWeekendOffIsNotPending_AndNothingWrongIsHealthy()
    {
        Assert.Equal(("Healthy", (string?)null), HostStanding.Of(false, 0, AgentUpdateState.Current, Now.AddDays(-2), Now));
        Assert.Equal(("Healthy", (string?)null), HostStanding.Of(false, 0, AgentUpdateState.Unknown, Now, Now));
    }
}

public class GetHostsHealthTests
{
    private readonly Mock<IHostRepository> _hosts = new();
    private readonly Mock<IPatchFailureRepository> _failures = new();
    private readonly Mock<IHostEventRepository> _events = new();

    public GetHostsHealthTests()
    {
        _failures.Setup(r => r.CountOutstandingByHostAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new Dictionary<Guid, int>());
        _events.Setup(r => r.GetHealthEventsSinceAsync(It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<HealthEventRecord>());
    }

    private GetHostsQueryHandler Handler()
    {
        var upgradePaths = new Mock<IUpgradePathRepository>();
        upgradePaths.Setup(r => r.GetInstallationPatchStatusesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<InstallationPatchStatus>());
        var vulnerabilities = new Mock<IVulnerabilityRepository>();
        vulnerabilities.Setup(r => r.GetApplicationCveMatchesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<ApplicationCveMatch>());
        vulnerabilities.Setup(r => r.GetOperatingSystemCveMatchesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<OperatingSystemCveMatch>());
        vulnerabilities.Setup(r => r.GetPackageCveMatchesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<PackageCveMatch>());
        var packages = new Mock<IAgentPackageRepository>();
        packages.Setup(r => r.GetLatestPerPlatformAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<AgentPackage>());

        return new GetHostsQueryHandler(
            _hosts.Object, upgradePaths.Object, vulnerabilities.Object, new Mock<IInstalledPackageRepository>().Object, packages.Object,
            _failures.Object, _events.Object);
    }

    [Fact]
    public async Task AnOutstandingFailureOrAnUnstableHostIsAProblem_AndAHostWithNeitherIsHealthy()
    {
        var failing = new Host("failing", "S1");
        var unstable = new Host("unstable", "S2");
        var fine = new Host("fine", "S4");
        foreach (var h in new[] { failing, unstable, fine })
        {
            h.RecordHeartbeat(HostStatus.Online);
        }

        _hosts.Setup(r => r.GetAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new[] { failing, unstable, fine });
        // Only outstanding failures are counted, in the database (PatchFailureRepositoryTests covers that half).
        _failures.Setup(r => r.CountOutstandingByHostAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new Dictionary<Guid, int>
        {
            [failing.Id] = 2,
            [Guid.NewGuid()] = 1,   // a host that is not on the list
        });
        var now = DateTimeOffset.UtcNow;
        _events.Setup(r => r.GetHealthEventsSinceAsync(It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>())).ReturnsAsync(new[]
        {
            new HealthEventRecord(unstable.Id, HostEventKind.HardwareErrorFatal, now.AddDays(-1)),
        });

        var result = (await Handler().Handle(new GetHostsQuery(), CancellationToken.None)).ToDictionary(h => h.Hostname);

        Assert.Equal("Problem", result["failing"].Health);
        Assert.Equal(2, result["failing"].FailedPatchCount);
        Assert.Equal("2 failed patches", result["failing"].HealthReason);
        Assert.Equal("Problem", result["unstable"].Health);
        Assert.True(result["unstable"].Unstable);
        Assert.Equal("Healthy", result["fine"].Health);
    }

    [Fact]
    public async Task WithNoFailuresOrEvents_HealthComesFromWhatTheHostItselfSays()
    {
        var host = new Host("old", "S1");
        _hosts.Setup(r => r.GetAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new[] { host });

        var result = await Handler().Handle(new GetHostsQuery(), CancellationToken.None);

        Assert.Equal("Pending", result.Single().Health);   // never seen
        Assert.Equal(0, result.Single().FailedPatchCount);
    }

    [Fact]
    public async Task TheListCarriesTheInventoryDetailsButOnlyAShortNameForThePerson()
    {
        var host = new Host("pc", "S1");
        host.RecordHeartbeat(HostStatus.Online);
        host.SetProfile("HTW", "Technology", "Adelaide", "Surface Laptop 7", "Alex Morgan", "alex.morgan@example.com");
        _hosts.Setup(r => r.GetAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new[] { host });

        var dto = (await Handler().Handle(new GetHostsQuery(), CancellationToken.None)).Single();

        Assert.Equal(("HTW", "Technology", "Adelaide", "Surface Laptop 7"), (dto.Organization, dto.Department, dto.Location, dto.Model));
        Assert.Equal("Alex M.", dto.AssignedShortName);
        Assert.True(dto.HasAssignedPerson);
        // The full name and the email are not on the DTO at all, whatever serializer reads it.
        var json = System.Text.Json.JsonSerializer.Serialize(dto);
        Assert.DoesNotContain("Morgan", json);
        Assert.DoesNotContain("example.com", json);
    }
}

public class HostProfileCommandTests
{
    private readonly Mock<IHostRepository> _hosts = new();
    private readonly Mock<IHostEventRecorder> _events = new();
    private readonly Mock<IUnitOfWork> _unit = new();

    [Fact]
    public async Task ABulkEditChangesOnlyTheNamedFields_RecordsWhoDidItAndSkipsWhatDidNotChange()
    {
        var a = new Host("a", "S1");
        var b = new Host("b", "S2");
        b.SetProfile(department: "HR");
        _hosts.Setup(r => r.GetByIdsAsync(It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<CancellationToken>())).ReturnsAsync(new[] { a, b });

        var updated = await new UpdateHostProfileCommandHandler(_hosts.Object, _events.Object, _unit.Object)
            .Handle(new UpdateHostProfileCommand([a.Id, b.Id], null, "HR", "Perth", "juan"), CancellationToken.None);

        Assert.Equal(2, updated);
        Assert.Equal(("HR", "Perth"), (a.Department, a.Location));
        _events.Verify(e => e.HostProfileChangedAsync(a.Id, "juan", "Department, Location", It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()), Times.Once);
        _events.Verify(e => e.HostProfileChangedAsync(b.Id, "juan", "Location", It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()), Times.Once);
        _unit.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);

        // Repeating it changes nothing, so it records nothing and saves nothing.
        _events.Invocations.Clear();
        _unit.Invocations.Clear();
        var again = await new UpdateHostProfileCommandHandler(_hosts.Object, _events.Object, _unit.Object)
            .Handle(new UpdateHostProfileCommand([a.Id, b.Id], null, "HR", "Perth", "juan"), CancellationToken.None);
        Assert.Equal(0, again);
        _events.VerifyNoOtherCalls();
        _unit.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public void TheBulkEditValidatorNeedsHostsAndSomethingToChange()
    {
        var v = new UpdateHostProfileCommandValidator();

        Assert.True(v.Validate(new UpdateHostProfileCommand([Guid.NewGuid()], null, "HR", null, "a")).IsValid);
        Assert.False(v.Validate(new UpdateHostProfileCommand([], null, "HR", null, "a")).IsValid);
        Assert.False(v.Validate(new UpdateHostProfileCommand([Guid.NewGuid()], null, null, null, "a")).IsValid);
        Assert.False(v.Validate(new UpdateHostProfileCommand([Guid.NewGuid()], null, new string('x', 101), null, "a")).IsValid);
    }

    [Fact]
    public async Task AnImportMatchesBySerialIgnoringCase_TheLastRowWins_EmptyCellsAreIgnored_AndNamesComeOnlyIfSent()
    {
        var a = new Host("a", "ABC123");
        a.SetProfile(department: "Old", location: "Keep me");
        var b = new Host("b", "XYZ789");
        _hosts.Setup(r => r.GetBySerialNumbersAsync(It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>())).ReturnsAsync(new[] { a, b });

        var result = await new ImportHostProfilesCommandHandler(_hosts.Object, _events.Object, _unit.Object).Handle(
            new ImportHostProfilesCommand(
            [
                new HostProfileRow("abc123", "Wrong"),                                        // superseded by the last row for this serial
                new HostProfileRow("ABC123", "Technology", Location: "", Model: "Surface Pro 8"),   // empty location: left alone
                new HostProfileRow("xyz789", Location: "Perth"),                                // no name or email in the request
                new HostProfileRow("NOPE", "HR"),                                              // no such host
            ],
            "juan"),
            CancellationToken.None);

        Assert.Equal(new ImportHostProfilesResult(Matched: 2, NotFound: 1, Updated: 2), result);
        Assert.Equal(("Technology", "Keep me", "Surface Pro 8"), (a.Department, a.Location, a.Model));
        Assert.Equal("Perth", b.Location);
        Assert.Null(a.AssignedName);
        Assert.Null(b.AssignedEmail);
        _events.Verify(e => e.HostProfileChangedAsync(a.Id, "juan", "Imported: Department, Model", It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task AnImportCanCarryNameAndEmailWhenTheAdministratorChoseThem()
    {
        var host = new Host("a", "S1");
        _hosts.Setup(r => r.GetBySerialNumbersAsync(It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>())).ReturnsAsync(new[] { host });

        await new ImportHostProfilesCommandHandler(_hosts.Object, _events.Object, _unit.Object).Handle(
            new ImportHostProfilesCommand([new HostProfileRow("S1", AssignedName: "Alex Morgan", AssignedEmail: "alex.morgan@example.com")], "juan"),
            CancellationToken.None);

        Assert.Equal("Alex Morgan", host.AssignedName);
        Assert.Equal("alex.morgan@example.com", host.AssignedEmail);
    }

    [Fact]
    public async Task AnImportThatWasSentThePersonColumns_ClearsThePersonOnARowWhereTheyAreEmpty_AndOneThatWasNotKeepsIt()
    {
        var returned = new Host("returned", "S1");
        returned.SetProfile(assignedName: "Alex Morgan", assignedEmail: "alex.morgan@example.com");
        var untouched = new Host("untouched", "S2");
        untouched.SetProfile(assignedName: "Sam Taylor", assignedEmail: "sam.taylor@example.com");
        _hosts.Setup(r => r.GetBySerialNumbersAsync(It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { returned, untouched });

        await new ImportHostProfilesCommandHandler(_hosts.Object, _events.Object, _unit.Object).Handle(
            new ImportHostProfilesCommand(
            [
                new HostProfileRow("S1", AssignedName: "", AssignedEmail: ""),   // ticked, and checked back in
                new HostProfileRow("S2", "HR"),                                   // the person columns were not sent
            ],
            "juan"),
            CancellationToken.None);

        Assert.Null(returned.AssignedName);
        Assert.Null(returned.AssignedEmail);
        Assert.Equal("Sam Taylor", untouched.AssignedName);
        _events.Verify(e => e.HostProfileChangedAsync(returned.Id, "juan", "Imported: Assigned name, Assigned email", It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ABulkEditCanClearTheAssignedPerson_AndThatAloneIsSomethingToChange()
    {
        var host = new Host("a", "S1");
        host.SetProfile(department: "HR", assignedName: "Alex Morgan", assignedEmail: "alex.morgan@example.com");
        _hosts.Setup(r => r.GetByIdsAsync(It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<CancellationToken>())).ReturnsAsync(new[] { host });
        var command = new UpdateHostProfileCommand([host.Id], null, null, null, "juan", ClearAssignedPerson: true);

        Assert.True(new UpdateHostProfileCommandValidator().Validate(command).IsValid);
        var updated = await new UpdateHostProfileCommandHandler(_hosts.Object, _events.Object, _unit.Object).Handle(command, CancellationToken.None);

        Assert.Equal(1, updated);
        Assert.Null(host.AssignedName);
        Assert.Null(host.AssignedEmail);
        Assert.Equal("HR", host.Department);
        _events.Verify(e => e.HostProfileChangedAsync(host.Id, "juan", "Assigned name, Assigned email", It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public void TheImportValidatorBoundsTheRequest()
    {
        var v = new ImportHostProfilesCommandValidator();

        Assert.True(v.Validate(new ImportHostProfilesCommand([new HostProfileRow("S1", "HR")], "a")).IsValid);
        Assert.False(v.Validate(new ImportHostProfilesCommand([], "a")).IsValid);
        Assert.False(v.Validate(new ImportHostProfilesCommand([new HostProfileRow("", "HR")], "a")).IsValid);
        Assert.False(v.Validate(new ImportHostProfilesCommand(
            Enumerable.Range(0, ImportHostProfilesCommandValidator.MaxRows + 1).Select(i => new HostProfileRow("S" + i)).ToList(), "a")).IsValid);
    }

    [Fact]
    public async Task RevealingRecordsWhoLookedAndReturnsTheFullDetails_ButANoPersonHostRecordsNothing()
    {
        var withPerson = new Host("a", "S1");
        withPerson.SetProfile(assignedName: "Alex Morgan", assignedEmail: "alex.morgan@example.com");
        var empty = new Host("b", "S2");
        _hosts.Setup(r => r.GetByIdAsync(withPerson.Id, It.IsAny<CancellationToken>())).ReturnsAsync(withPerson);
        _hosts.Setup(r => r.GetByIdAsync(empty.Id, It.IsAny<CancellationToken>())).ReturnsAsync(empty);
        var handler = new RevealAssignedPersonCommandHandler(_hosts.Object, _events.Object, _unit.Object);

        var revealed = await handler.Handle(new RevealAssignedPersonCommand(withPerson.Id, "juan"), CancellationToken.None);

        Assert.Equal(new AssignedPersonDto("Alex Morgan", "alex.morgan@example.com"), revealed);
        _events.Verify(e => e.AssignedPersonRevealedAsync(withPerson.Id, "juan", It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()), Times.Once);
        _unit.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);

        _events.Invocations.Clear();
        var nothing = await handler.Handle(new RevealAssignedPersonCommand(empty.Id, "juan"), CancellationToken.None);
        Assert.Equal(new AssignedPersonDto(null, null), nothing);
        _events.VerifyNoOtherCalls();

        await Assert.ThrowsAsync<NotFoundException>(() => handler.Handle(new RevealAssignedPersonCommand(Guid.NewGuid(), "juan"), CancellationToken.None));
    }
}
