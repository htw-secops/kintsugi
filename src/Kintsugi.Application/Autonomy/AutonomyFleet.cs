using Kintsugi.Application.DeviceHealth;
using Kintsugi.Domain.Enums;

namespace Kintsugi.Application.Autonomy;

/// <summary>
/// The fleet split the Autonomy dial and donut draw, and how the unstable devices relate to patching.
/// </summary>
/// <param name="NothingToPatch">Devices with nothing holding them up (this includes a device that is off but has nothing waiting).</param>
/// <param name="Disconnected">Not connected, with an update waiting: Kintsugi cannot reach it, so nothing can be tried.</param>
/// <param name="Restart">Updated, restart pending.</param>
/// <param name="Problem">A patch failed and is still outstanding.</param>
/// <param name="Unstable">Devices <see cref="HostHealthAssessor"/> calls unstable. They are counted in the four above too.</param>
/// <param name="UnstableNearPatch">Unstable devices that shut down unexpectedly within <see cref="AutonomyFleetAnalyzer.NearPatch"/> of a patch.</param>
/// <param name="UnstableOnProblem">Unstable devices that are also in <paramref name="Problem"/>.</param>
public record AutonomyFleetDto(
    int NothingToPatch,
    int Disconnected,
    int Restart,
    int Problem,
    int Unstable,
    int UnstableNearPatch,
    int UnstableOnProblem);

/// <summary>
/// Pure, so it reads the same causes the page lists. A device is in exactly one of the four states, worst first:
/// Problem, then Restart, then Disconnected, then NothingToPatch. Unstable is separate: it is a clue to why a patch
/// failed or never finished, not a state of its own.
/// </summary>
public static class AutonomyFleetAnalyzer
{
    /// <summary>The agents do not report when a patch starts, only when it ends, so "during a patch" cannot be seen.
    /// A shutdown this close to a patch event on the same device is the nearest honest signal, and the page says so.</summary>
    public static readonly TimeSpan NearPatch = TimeSpan.FromHours(2);

    public static AutonomyFleetDto Analyze(
        int totalHosts,
        IReadOnlyList<CauseEntry> causes,
        IReadOnlyList<HostHealthAssessment> health,
        IReadOnlyList<HealthEventRecord> healthEvents,
        IReadOnlyList<ClassifiedPatch> patches)
    {
        HashSet<Guid> Of(Func<CauseEntry, bool> pick) =>
            causes.Where(pick).SelectMany(c => c.Devices).Select(d => d.HostId).ToHashSet();

        var problem = Of(c => c.Category == CauseCategory.Failure);
        var restart = Of(c => c.Category == CauseCategory.Restart);
        restart.ExceptWith(problem);
        var disconnected = Of(c => c.Category == CauseCategory.Disconnected);
        disconnected.ExceptWith(problem);
        disconnected.ExceptWith(restart);

        var unstable = health.Where(a => a.Health == HostHealth.Unstable).Select(a => a.HostId).ToHashSet();
        var nearPatch = unstable.Count(id =>
            healthEvents.Any(e => e.HostId == id && e.Kind == HostEventKind.UnexpectedShutdown
                && patches.Any(p => p.HostId == id && (p.OccurredUtc - e.OccurredUtc).Duration() <= NearPatch)));

        return new AutonomyFleetDto(
            Math.Max(0, totalHosts - problem.Count - restart.Count - disconnected.Count),
            disconnected.Count, restart.Count, problem.Count,
            unstable.Count, nearPatch, unstable.Count(problem.Contains));
    }
}
