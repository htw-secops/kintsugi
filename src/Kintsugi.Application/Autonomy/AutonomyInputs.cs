using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Application.PatchFailures;
using Kintsugi.Domain.Entities;
using Kintsugi.Domain.Enums;

namespace Kintsugi.Application.Autonomy;

/// <summary>What the cause analysis and the KPIs are worked out from, read once and the same way for the
/// page, the daily snapshot and Patch now by cause.</summary>
public record AutonomyInputs(
    IReadOnlyList<Host> Hosts,
    IReadOnlyList<PatchFailureDto> OpenFailures,
    IReadOnlyDictionary<Guid, int> AppUpdates)
{
    public static async Task<AutonomyInputs> LoadAsync(
        IHostRepository hosts,
        IPatchFailureRepository failures,
        IUpgradePathRepository upgradePaths,
        CancellationToken cancellationToken)
    {
        var all = await hosts.GetAllAsync(cancellationToken);
        var ids = all.Select(h => h.Id).ToHashSet();
        var open = (await failures.GetAllAsync(cancellationToken))
            .Where(f => f.Resolution == PatchFailureResolution.Outstanding && ids.Contains(f.HostId))
            .ToList();
        var statuses = await upgradePaths.GetInstallationPatchStatusesAsync(cancellationToken);
        var appUpdates = statuses
            .Where(s => s.UpdateAvailable == true)
            .GroupBy(s => s.HostId)
            .ToDictionary(g => g.Key, g => g.Count());

        return new AutonomyInputs(all, open, appUpdates);
    }

    public IReadOnlyList<CauseEntry> Causes(DateTimeOffset now) => AutonomyCauseAnalyzer.Analyze(Hosts, OpenFailures, AppUpdates, now);
}
