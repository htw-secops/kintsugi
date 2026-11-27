using MediatR;
using Kintsugi.Application.Common.Interfaces;

namespace Kintsugi.Application.Autonomy.Queries.GetKevBacklog;

public class GetKevBacklogQueryHandler : IRequestHandler<GetKevBacklogQuery, KevBacklogDto>
{
    public const string TargetText = "A device is overdue when an exploited CVE on it has been on CISA's list for more than 14 days. Counts every device running an affected version, patched or not.";

    private readonly IHostRepository _hosts;
    private readonly IUpgradePathRepository _upgradePaths;
    private readonly IVulnerabilityRepository _vulnerabilities;
    private readonly IInstalledPackageRepository _installedPackages;
    private readonly IKevBacklogSnapshotRepository _snapshots;

    public GetKevBacklogQueryHandler(
        IHostRepository hosts,
        IUpgradePathRepository upgradePaths,
        IVulnerabilityRepository vulnerabilities,
        IInstalledPackageRepository installedPackages,
        IKevBacklogSnapshotRepository snapshots)
    {
        _hosts = hosts;
        _upgradePaths = upgradePaths;
        _vulnerabilities = vulnerabilities;
        _installedPackages = installedPackages;
        _snapshots = snapshots;
    }

    public async Task<KevBacklogDto> Handle(GetKevBacklogQuery request, CancellationToken cancellationToken)
    {
        var days = request.Days is >= 7 and <= 365 ? request.Days : 60;
        var now = DateTimeOffset.UtcNow;
        var today = new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero);

        var stored = await _snapshots.GetSinceAsync(today.AddDays(-days), cancellationToken);
        var series = stored
            .Where(s => s.SnapshotDateUtc < today)
            .Select(s => new KevBacklogPointDto(s.SnapshotDateUtc, s.DevicesOpen, s.DevicesWithinTarget, s.DevicesOverdue, s.DevicesDueWithin7Days, s.OverdueNotSeen7Days, s.TopCveId, s.TopCveDevices))
            .ToList();

        var p = await KevBacklog.LoadAsync(await _hosts.GetAllAsync(cancellationToken), _upgradePaths, _vulnerabilities, _installedPackages, now, cancellationToken);
        var live = new KevBacklogPointDto(today, p.DevicesOpen, p.WithinTarget, p.Overdue, p.DueWithin7Days, p.OverdueNotSeen7Days, p.TopCveId, p.TopCveDevices);

        return new KevBacklogDto(series, live, TargetText);
    }
}
