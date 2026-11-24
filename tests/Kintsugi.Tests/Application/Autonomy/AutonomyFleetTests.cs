using Kintsugi.Application.Autonomy;
using Kintsugi.Application.DeviceHealth;
using Kintsugi.Domain.Enums;

namespace Kintsugi.Tests.Application.Autonomy;

public class AutonomyFleetTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 24, 10, 0, 0, TimeSpan.Zero);

    private static CauseEntry Cause(CauseCategory category, params Guid[] hosts) =>
        new(category.ToString(), category.ToString(), category, true, null, null,
            hosts.Select(h => new CauseDevice(h, "h", true, false, 1, "x")).ToList());

    private static HostHealthAssessment Unstable(Guid id) =>
        new(id, HostHealth.Unstable, 3, 3, 0, 0, Now);

    [Fact]
    public void ADeviceIsInOneStateOnly_WorstFirst_AndTheRestHaveNothingToPatch()
    {
        var (failed, restart, off, both) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

        var fleet = AutonomyFleetAnalyzer.Analyze(
            10,
            [Cause(CauseCategory.Failure, failed, both), Cause(CauseCategory.Restart, restart, both), Cause(CauseCategory.Disconnected, off, both, failed)],
            [], [], []);

        // "both" is in all three causes and counts once, as a problem.
        Assert.Equal((6, 1, 1, 2), (fleet.NothingToPatch, fleet.Disconnected, fleet.Restart, fleet.Problem));
    }

    [Fact]
    public void AnUnstableDeviceIsAClueNotAState_AndIsCountedWhereItAlsoFailedToPatch()
    {
        var (a, b) = (Guid.NewGuid(), Guid.NewGuid());

        var fleet = AutonomyFleetAnalyzer.Analyze(5, [Cause(CauseCategory.Failure, a)], [Unstable(a), Unstable(b)], [], []);

        Assert.Equal((2, 1), (fleet.Unstable, fleet.UnstableOnProblem));
        Assert.Equal(4, fleet.NothingToPatch);
    }

    [Fact]
    public void ADeviceThatShutDownWithinTwoHoursOfAPatchIsNearAPatch_OneFurtherAwayIsNot()
    {
        var (near, far) = (Guid.NewGuid(), Guid.NewGuid());
        var patches = new[]
        {
            new ClassifiedPatch(near, "Windows", Now, false),
            new ClassifiedPatch(far, "Windows", Now, false),
        };
        var events = new[]
        {
            new HealthEventRecord(near, HostEventKind.UnexpectedShutdown, Now.AddMinutes(-90)),
            new HealthEventRecord(far, HostEventKind.UnexpectedShutdown, Now.AddHours(-5)),
        };

        var fleet = AutonomyFleetAnalyzer.Analyze(4, [], [Unstable(near), Unstable(far)], events, patches);

        Assert.Equal(1, fleet.UnstableNearPatch);
    }
}
