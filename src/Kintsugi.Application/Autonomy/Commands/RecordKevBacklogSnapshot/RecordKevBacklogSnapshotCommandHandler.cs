using MediatR;
using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Domain.Entities;

namespace Kintsugi.Application.Autonomy.Commands.RecordKevBacklogSnapshot;

public class RecordKevBacklogSnapshotCommandHandler : IRequestHandler<RecordKevBacklogSnapshotCommand, bool>
{
    private readonly IHostRepository _hosts;
    private readonly IUpgradePathRepository _upgradePaths;
    private readonly IVulnerabilityRepository _vulnerabilities;
    private readonly IInstalledPackageRepository _installedPackages;
    private readonly IKevBacklogSnapshotRepository _snapshots;
    private readonly IUnitOfWork _unitOfWork;

    public RecordKevBacklogSnapshotCommandHandler(
        IHostRepository hosts,
        IUpgradePathRepository upgradePaths,
        IVulnerabilityRepository vulnerabilities,
        IInstalledPackageRepository installedPackages,
        IKevBacklogSnapshotRepository snapshots,
        IUnitOfWork unitOfWork)
    {
        _hosts = hosts;
        _upgradePaths = upgradePaths;
        _vulnerabilities = vulnerabilities;
        _installedPackages = installedPackages;
        _snapshots = snapshots;
        _unitOfWork = unitOfWork;
    }

    public async Task<bool> Handle(RecordKevBacklogSnapshotCommand request, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        if (await _snapshots.ExistsForDateAsync(now, cancellationToken))
        {
            return false;
        }

        // No fleet, no point: a zero here would read as a clean day rather than "nothing to measure".
        if ((await _hosts.GetAllAsync(cancellationToken)).Count == 0)
        {
            return false;
        }

        var p = await KevBacklog.LoadAsync(_hosts, _upgradePaths, _vulnerabilities, _installedPackages, now, cancellationToken);
        await _snapshots.AddAsync(
            KevBacklogSnapshot.Record(now, p.DevicesOpen, p.WithinTarget, p.Overdue, p.DueWithin7Days, p.OverdueNotSeen7Days, p.TopCveId, p.TopCveDevices),
            cancellationToken);
        await _unitOfWork.SaveChangesAsync(cancellationToken);
        return true;
    }
}
