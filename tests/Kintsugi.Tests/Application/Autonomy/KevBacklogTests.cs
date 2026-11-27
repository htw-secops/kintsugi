using Moq;
using Kintsugi.Application.Autonomy;
using Kintsugi.Application.Autonomy.Commands.RecordKevBacklogSnapshot;
using Kintsugi.Application.Autonomy.Queries.GetKevBacklog;
using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Application.Vulnerabilities;
using Kintsugi.Domain.Entities;
using Kintsugi.Domain.Enums;

namespace Kintsugi.Tests.Application.Autonomy;

public class KevBacklogTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 27, 12, 0, 0, TimeSpan.Zero);

    /// <summary>An exploited CVE that CISA added this many days ago; null for one with no added date.</summary>
    private static Vulnerability Kev(string id, int? addedDaysAgo)
    {
        var v = Vulnerability.Create(id);
        v.ApplyKevEntry(addedDaysAgo is { } d ? Now.AddDays(-d) : null, null, false, null, null);
        return v;
    }

    private static Host SeenHost(string name, DateTimeOffset lastSeen)
    {
        var host = new Host(name, "S-" + name, "Windows 11 Pro");
        host.RecordHeartbeat(HostStatus.Online);
        typeof(Host).GetProperty(nameof(Host.LastSeenUtc))!.SetValue(host, lastSeen);
        return host;
    }

    [Fact]
    public void ADeviceIsOverdueWhenAnOpenExploitedCveIsOlderThanTheTarget_AndCountedOnce()
    {
        var late = SeenHost("late", Now.AddHours(-1));
        var both = SeenHost("both", Now.AddHours(-1));
        var fine = SeenHost("fine", Now.AddHours(-1));
        var clean = SeenHost("clean", Now.AddHours(-1));
        var exploited = new[] { Kev("CVE-1", 17), Kev("CVE-2", 24), Kev("CVE-3", 3) };
        var unpatched = new Dictionary<Guid, HashSet<string>>
        {
            [late.Id] = ["CVE-1"],
            [both.Id] = ["CVE-1", "CVE-2", "CVE-NOT-EXPLOITED"],   // two overdue CVEs, still one device
            [fine.Id] = ["CVE-3"],
        };

        var p = KevBacklog.Compute([late, both, fine, clean], unpatched, exploited, Now);

        Assert.Equal(3, p.DevicesOpen);        // clean has none; a CVE not on the list does not count
        Assert.Equal(2, p.Overdue);
        Assert.Equal(1, p.WithinTarget);
        Assert.Equal(p.DevicesOpen, p.WithinTarget + p.Overdue);
        Assert.Equal("CVE-1", p.TopCveId);     // on two devices
        Assert.Equal(2, p.TopCveDevices);
    }

    [Fact]
    public void TheFourteenthDayIsStillWithinTarget_TheFifteenthIsOverdue()
    {
        var host = SeenHost("pc", Now);
        var unpatched = new Dictionary<Guid, HashSet<string>> { [host.Id] = ["CVE-1"] };

        var dueToday = KevBacklog.Compute([host], unpatched, [Kev("CVE-1", 14)], Now);
        var dueYesterday = KevBacklog.Compute([host], unpatched, [Kev("CVE-1", 15)], Now);

        Assert.Equal(0, dueToday.Overdue);
        Assert.Equal(1, dueYesterday.Overdue);
    }

    [Fact]
    public void WithinTargetAndDueInTheNextSevenDaysAreCountedApart()
    {
        var soon = SeenHost("soon", Now);
        var later = SeenHost("later", Now);
        var exploited = new[] { Kev("CVE-S", 7), Kev("CVE-L", 6) };
        var unpatched = new Dictionary<Guid, HashSet<string>> { [soon.Id] = ["CVE-S"], [later.Id] = ["CVE-L"] };

        var p = KevBacklog.Compute([soon, later], unpatched, exploited, Now);

        Assert.Equal(2, p.WithinTarget);
        Assert.Equal(1, p.DueWithin7Days);
    }

    [Fact]
    public void OverdueDevicesNotSeenForSevenDaysAreCountedApart()
    {
        var off = SeenHost("off", Now.AddDays(-7));
        var on = SeenHost("on", Now.AddDays(-6));
        var exploited = new[] { Kev("CVE-1", 19) };
        var unpatched = new Dictionary<Guid, HashSet<string>> { [off.Id] = ["CVE-1"], [on.Id] = ["CVE-1"] };

        var p = KevBacklog.Compute([off, on], unpatched, exploited, Now);

        Assert.Equal(2, p.Overdue);
        Assert.Equal(1, p.OverdueNotSeen7Days);
    }

    [Fact]
    public void ACveWithNoAddedDateNeverMakesADeviceOverdue_AndNoFleetGivesZeroes()
    {
        var host = SeenHost("pc", Now);
        var unpatched = new Dictionary<Guid, HashSet<string>> { [host.Id] = ["CVE-1"] };

        var p = KevBacklog.Compute([host], unpatched, [Kev("CVE-1", null)], Now);

        Assert.Equal((1, 0, 1), (p.DevicesOpen, p.Overdue, p.WithinTarget));
        Assert.Equal(new KevBacklogPoint(0, 0, 0, 0, 0, null, 0), KevBacklog.Compute([], new Dictionary<Guid, HashSet<string>>(), [], Now));
    }

    [Fact]
    public async Task ARecordedDayKeepsItsCounts_AndIsNotRecordedTwiceInADay()
    {
        var host = SeenHost("pc", DateTimeOffset.UtcNow);
        var (hosts, upgradePaths, vulnerabilities, packages) = Sources(host);
        var snapshots = new Mock<IKevBacklogSnapshotRepository>();
        KevBacklogSnapshot? written = null;
        snapshots.Setup(r => r.AddAsync(It.IsAny<KevBacklogSnapshot>(), It.IsAny<CancellationToken>())).Callback<KevBacklogSnapshot, CancellationToken>((s, _) => written = s).Returns(Task.CompletedTask);
        snapshots.SetupSequence(r => r.ExistsForDateAsync(It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>())).ReturnsAsync(false).ReturnsAsync(true);
        var handler = new RecordKevBacklogSnapshotCommandHandler(hosts.Object, upgradePaths.Object, vulnerabilities.Object, packages.Object, snapshots.Object, new Mock<IUnitOfWork>().Object);

        Assert.True(await handler.Handle(new RecordKevBacklogSnapshotCommand(), CancellationToken.None));
        Assert.False(await handler.Handle(new RecordKevBacklogSnapshotCommand(), CancellationToken.None));

        Assert.Equal((1, 1, 0, "CVE-1"), (written!.DevicesOpen, written.DevicesOverdue, written.DevicesWithinTarget, written.TopCveId));
        snapshots.Verify(r => r.AddAsync(It.IsAny<KevBacklogSnapshot>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task TheQueryReturnsRecordedDaysBeforeTodayAndTodayLive_NothingMadeUp()
    {
        var host = SeenHost("pc", DateTimeOffset.UtcNow);
        var (hosts, upgradePaths, vulnerabilities, packages) = Sources(host);
        var today = new DateTimeOffset(DateTimeOffset.UtcNow.UtcDateTime.Date, TimeSpan.Zero);
        var snapshots = new Mock<IKevBacklogSnapshotRepository>();
        snapshots.Setup(r => r.GetSinceAsync(It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>())).ReturnsAsync(new[]
        {
            KevBacklogSnapshot.Record(today.AddDays(-1), 5, 3, 2, 1, 1, "CVE-1", 4),
            KevBacklogSnapshot.Record(today, 9, 9, 0, 0, 0, null, 0),   // today's stored row is replaced by the live point
        });
        var handler = new GetKevBacklogQueryHandler(hosts.Object, upgradePaths.Object, vulnerabilities.Object, packages.Object, snapshots.Object);

        var result = await handler.Handle(new GetKevBacklogQuery(), CancellationToken.None);

        Assert.Single(result.Series);
        Assert.Equal(today.AddDays(-1), result.Series[0].DateUtc);
        Assert.Equal(1, result.Today.DevicesOpen);
        Assert.Equal(today, result.Today.DateUtc);
        Assert.Contains("CISA", result.Target);
    }

    /// <summary>One host with one overdue exploited CVE, through the real per-host exposure logic.</summary>
    private static (Mock<IHostRepository>, Mock<IUpgradePathRepository>, Mock<IVulnerabilityRepository>, Mock<IInstalledPackageRepository>) Sources(Host host)
    {
        var hosts = new Mock<IHostRepository>();
        hosts.Setup(r => r.GetAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new[] { host });
        var upgradePaths = new Mock<IUpgradePathRepository>();
        upgradePaths.Setup(r => r.GetInstallationPatchStatusesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new[]
        {
            new InstallationPatchStatus(host.Id, "Chrome", "1.0", true),
        });
        var vulnerabilities = new Mock<IVulnerabilityRepository>();
        vulnerabilities.Setup(r => r.GetApplicationCveMatchesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new[] { new ApplicationCveMatch("chrome", "1.0", "CVE-1") });
        vulnerabilities.Setup(r => r.GetOperatingSystemCveMatchesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<OperatingSystemCveMatch>());
        vulnerabilities.Setup(r => r.GetPackageCveMatchesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<PackageCveMatch>());
        vulnerabilities.Setup(r => r.GetKnownExploitedAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new[] { Kev("CVE-1", 20) });
        return (hosts, upgradePaths, vulnerabilities, new Mock<IInstalledPackageRepository>());
    }
}
