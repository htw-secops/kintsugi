using Moq;
using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Application.DeviceHealth;
using Kintsugi.Application.Hosts;
using Kintsugi.Application.Hosts.Queries.GetHosts;
using Kintsugi.Application.PatchFailures;
using Kintsugi.Application.UpgradePaths;
using Kintsugi.Application.Vulnerabilities;
using Kintsugi.Domain.Entities;
using Kintsugi.Domain.Enums;
using Kintsugi.Domain.Exceptions;

namespace Kintsugi.Tests.Application.Hosts;

public class WindowsPatchComplianceTests
{
    // November 2026's cumulative update: revision 9974 on each build, released on the 10th.
    private static readonly DateTimeOffset Released = new(2026, 11, 10, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Now = new(2026, 11, 27, 0, 0, 0, TimeSpan.Zero);   // 17 days on: grace (14) is over

    private static readonly IReadOnlyList<WindowsPatchBaseline> Baselines =
    [
        WindowsPatchBaseline.Create("26100", 9974, Released),
        WindowsPatchBaseline.Create("26200", 9974, Released),
        WindowsPatchBaseline.Create("26300", 9974, Released),
    ];

    private static (string State, string Reason)? Of(string? os, string? versionId, DateTimeOffset? lastSeen, DateTimeOffset? now = null) =>
        WindowsPatchCompliance.Of(os, versionId, lastSeen, Baselines, now ?? Now);

    [Fact]
    public void ABuildBelowTheMinimumAfterTheGraceIsNonCompliant()
    {
        var result = Of("Windows 11 Pro 25H2 (26200)", "26200.9457", Now.AddHours(-2));

        Assert.Equal(WindowsPatchCompliance.NonCompliant, result!.Value.State);
        Assert.Equal("Windows 26200.9457 is behind 26200.9974", result.Value.Reason);
    }

    [Fact]
    public void ABuildAtTheMinimumIsCompliant()
    {
        Assert.Equal(WindowsPatchCompliance.Compliant, Of("Windows 11 Pro 26H1 (26300)", "26300.9974", Now.AddHours(-2))!.Value.State);
        Assert.Equal(WindowsPatchCompliance.Compliant, Of("Windows 11 Pro 26H1 (26300)", "26300.10200", Now.AddHours(-2))!.Value.State);
    }

    [Fact]
    public void BelowTheMinimumInsideTheGraceIsOnlyDue()
    {
        var inGrace = Released.AddDays(13);

        Assert.Equal(WindowsPatchCompliance.Due, Of("Windows 11 Pro (26200)", "26200.9457", inGrace.AddHours(-1), inGrace)!.Value.State);
        Assert.Equal(WindowsPatchCompliance.NonCompliant, Of("Windows 11 Pro (26200)", "26200.9457", Released.AddDays(14), Released.AddDays(14))!.Value.State);
    }

    [Fact]
    public void AHostNotSeenForSevenDaysIsStaleWhateverItLastReported()
    {
        Assert.Equal(WindowsPatchCompliance.Stale, Of("Windows 11 Pro (26200)", "26200.9974", Now.AddDays(-30))!.Value.State);
        Assert.Equal(WindowsPatchCompliance.Stale, Of("Windows 11 Pro (26200)", "26200.9457", null)!.Value.State);
        Assert.Equal(WindowsPatchCompliance.NonCompliant, Of("Windows 11 Pro (26200)", "26200.9457", Now.AddDays(-6))!.Value.State);
    }

    [Theory]
    [InlineData("macOS 15.1", "26200.9457")]          // not Windows
    [InlineData(null, "26200.9457")]                  // nothing reported
    [InlineData("Windows 11 Pro (26200)", null)]      // an agent that sends no revision
    [InlineData("Windows 11 Pro (26200)", "26200")]   // no revision in it
    [InlineData("Windows 11 Pro (26200)", "26200.x")] // unreadable
    [InlineData("Windows 11 Pro (22631)", "22631.4317")] // a build with no baseline
    public void NothingToJudgeIsNoVerdict_NotCompliant(string? os, string? versionId)
    {
        Assert.Null(Of(os, versionId, Now.AddHours(-1)));
    }

    [Fact]
    public void TheLongFormOfTheVersionIsReadToo()
    {
        Assert.Equal(WindowsPatchCompliance.NonCompliant, Of("Windows 11 Pro (26200)", "10.0.26200.9457", Now.AddHours(-1))!.Value.State);
    }

    [Fact]
    public void ABaselineMustBeAWellFormedBuildAndGrace()
    {
        Assert.Throws<DomainException>(() => WindowsPatchBaseline.Create("abc", 1, Released));
        Assert.Throws<DomainException>(() => WindowsPatchBaseline.Create("26200", -1, Released));
        Assert.Throws<DomainException>(() => WindowsPatchBaseline.Create("26200", 1, Released, graceDays: -1));
    }

    [Fact]
    public void OutOfComplianceIsPendingOnTheHealthColour_ButDueAndCompliantAreNot()
    {
        var late = (WindowsPatchCompliance.NonCompliant, "Windows 26200.9457 is behind 26200.9974");
        var due = (WindowsPatchCompliance.Due, "x");

        var (state, reason) = HostStanding.Of(0, AgentUpdateState.Current, Now.AddHours(-1), Now, late);

        Assert.Equal(HostStanding.Pending, state);
        Assert.Equal("Windows 26200.9457 is behind 26200.9974", reason);
        Assert.Equal(HostStanding.Healthy, HostStanding.Of(0, AgentUpdateState.Current, Now.AddHours(-1), Now, due).State);
        Assert.Equal(HostStanding.Healthy, HostStanding.Of(0, AgentUpdateState.Current, Now.AddHours(-1), Now, null).State);
        // A failed patch is still the worse colour.
        Assert.Equal(HostStanding.Problem, HostStanding.Of(1, AgentUpdateState.Current, Now.AddHours(-1), Now, late).State);
    }

    [Fact]
    public async Task TheHostsListShowsAnUnpatchedWindowsHostAsPendingWithItsCompliance()
    {
        var behind = new Host("CBR-SP7-4114653", "S1", "Windows 11 Pro 25H2 (26200)", operatingSystemVersionId: "26200.9457");
        var current = new Host("current", "S2", "Windows 11 Pro 26H1 (26300)", operatingSystemVersionId: "26300.9974");
        var mac = new Host("mac", "S3", "macOS 15.1");
        foreach (var host in new[] { behind, current, mac })
        {
            host.RecordHeartbeat(HostStatus.Online);
        }

        var hosts = new Mock<IHostRepository>();
        hosts.Setup(r => r.GetAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new[] { behind, current, mac });
        var upgradePaths = new Mock<IUpgradePathRepository>();
        upgradePaths.Setup(r => r.GetInstallationPatchStatusesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<InstallationPatchStatus>());
        var vulnerabilities = new Mock<IVulnerabilityRepository>();
        vulnerabilities.Setup(r => r.GetApplicationCveMatchesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<ApplicationCveMatch>());
        vulnerabilities.Setup(r => r.GetOperatingSystemCveMatchesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<OperatingSystemCveMatch>());
        vulnerabilities.Setup(r => r.GetPackageCveMatchesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<PackageCveMatch>());
        var packages = new Mock<IAgentPackageRepository>();
        packages.Setup(r => r.GetLatestPerPlatformAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<AgentPackage>());
        var failures = new Mock<IPatchFailureRepository>();
        failures.Setup(r => r.CountOutstandingByHostAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new Dictionary<Guid, int>());
        var events = new Mock<IHostEventRepository>();
        events.Setup(r => r.GetHealthEventsSinceAsync(It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<HealthEventRecord>());
        // Effective long enough ago that the grace is over whenever this test runs.
        var baselines = new Mock<IWindowsPatchBaselineRepository>();
        baselines.Setup(r => r.GetAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new[]
        {
            WindowsPatchBaseline.Create("26200", 9974, DateTimeOffset.UtcNow.AddDays(-60)),
            WindowsPatchBaseline.Create("26300", 9974, DateTimeOffset.UtcNow.AddDays(-60)),
        });

        var result = (await new GetHostsQueryHandler(
                hosts.Object, upgradePaths.Object, vulnerabilities.Object, new Mock<IInstalledPackageRepository>().Object,
                packages.Object, failures.Object, events.Object, baselines.Object)
            .Handle(new GetHostsQuery(), CancellationToken.None)).ToDictionary(h => h.Hostname);

        Assert.Equal("Pending", result["CBR-SP7-4114653"].Health);
        Assert.Equal(WindowsPatchCompliance.NonCompliant, result["CBR-SP7-4114653"].PatchCompliance);
        Assert.Equal("Healthy", result["current"].Health);
        Assert.Equal(WindowsPatchCompliance.Compliant, result["current"].PatchCompliance);
        Assert.Null(result["mac"].PatchCompliance);
    }
}
