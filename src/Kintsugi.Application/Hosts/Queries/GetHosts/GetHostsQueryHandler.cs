using MediatR;
using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Application.DeviceHealth;
using Kintsugi.Application.Vulnerabilities;
using Kintsugi.Domain.Entities;
using Kintsugi.Domain.Enums;

namespace Kintsugi.Application.Hosts.Queries.GetHosts;

public class GetHostsQueryHandler : IRequestHandler<GetHostsQuery, IReadOnlyList<HostDto>>
{
    private readonly IHostRepository _hostRepository;
    private readonly IUpgradePathRepository _upgradePathRepository;
    private readonly IVulnerabilityRepository _vulnerabilityRepository;
    private readonly IInstalledPackageRepository _installedPackageRepository;
    private readonly IAgentPackageRepository _agentPackageRepository;
    private readonly IPatchFailureRepository _patchFailureRepository;
    private readonly IHostEventRepository _hostEventRepository;
    private readonly IWindowsPatchBaselineRepository _windowsPatchBaselineRepository;

    /// <param name="patchFailureRepository">With <paramref name="hostEventRepository"/>, what the Health column is
    /// worked out from: the patch failures still outstanding and the device-health verdict. Required, not optional:
    /// a missing registration would otherwise quietly paint every host green.</param>
    /// <param name="windowsPatchBaselineRepository">The minimum update revision per Windows build, for the
    /// patch-compliance verdict on each Windows host.</param>
    public GetHostsQueryHandler(
        IHostRepository hostRepository,
        IUpgradePathRepository upgradePathRepository,
        IVulnerabilityRepository vulnerabilityRepository,
        IInstalledPackageRepository installedPackageRepository,
        IAgentPackageRepository agentPackageRepository,
        IPatchFailureRepository patchFailureRepository,
        IHostEventRepository hostEventRepository,
        IWindowsPatchBaselineRepository windowsPatchBaselineRepository)
    {
        _windowsPatchBaselineRepository = windowsPatchBaselineRepository;
        _patchFailureRepository = patchFailureRepository;
        _hostEventRepository = hostEventRepository;
        _hostRepository = hostRepository;
        _upgradePathRepository = upgradePathRepository;
        _vulnerabilityRepository = vulnerabilityRepository;
        _installedPackageRepository = installedPackageRepository;
        _agentPackageRepository = agentPackageRepository;
    }

    public async Task<IReadOnlyList<HostDto>> Handle(GetHostsQuery request, CancellationToken cancellationToken)
    {
        var hosts = await _hostRepository.GetAllAsync(cancellationToken);

        var patchStatuses = await _upgradePathRepository.GetInstallationPatchStatusesAsync(cancellationToken);
        var appCveMatches = await _vulnerabilityRepository.GetApplicationCveMatchesAsync(cancellationToken);
        var osCveMatches = await _vulnerabilityRepository.GetOperatingSystemCveMatchesAsync(cancellationToken);
        var packageCveMatches = await _vulnerabilityRepository.GetPackageCveMatchesAsync(cancellationToken);

        var appUpdateCounts = BuildAppUpdateCountsByHost(patchStatuses);

        var unpatched = new Dictionary<Guid, HashSet<string>>();
        var patched = new Dictionary<Guid, HashSet<string>>();

        HostCveExposure.AddApplicationCveCounts(patchStatuses, appCveMatches, unpatched, patched);
        HostCveExposure.AddOperatingSystemCveCounts(hosts, osCveMatches, unpatched, patched);
        await HostCveExposure.AddPackageCveCountsAsync(_installedPackageRepository, hosts, packageCveMatches, unpatched, patched, cancellationToken);

        // One read of the newest package per platform, then a comparison per host — see
        // AgentVersionDrift for why it is per platform and where "not updating" comes from.
        var latestByPlatform = (await _agentPackageRepository.GetLatestPerPlatformAsync(cancellationToken)
                ?? Array.Empty<AgentPackage>())
            .ToDictionary(p => p.Platform, p => new PublishedAgent(p.Version, p.CreatedAtUtc), StringComparer.OrdinalIgnoreCase);
        var now = DateTimeOffset.UtcNow;
        var known = hosts.Select(h => h.Id).ToHashSet();

        // The same two sources the Autonomy page reads, so a host the Hosts list calls a Problem is one that page
        // counts too: patch failures nobody has dealt with, and the device-health verdict over the last 30 days. The
        // failures are counted in the database rather than loaded whole, because this list is polled every 30 seconds
        // by every open tab (AutonomyInputs loads them whole because it needs the rows, not just the count).
        var failedByHost = await _patchFailureRepository.CountOutstandingByHostAsync(cancellationToken);
        var unstableHosts = HostHealthAssessor
            .AssessAll(
                (await _hostEventRepository.GetHealthEventsSinceAsync(now.AddDays(-30), cancellationToken)).Where(e => known.Contains(e.HostId)),
                now)
            .Where(a => a.Health == HostHealth.Unstable)
            .Select(a => a.HostId)
            .ToHashSet();

        // A handful of rows, read once per list. Required for the same reason as the two above: without it
        // no Windows host would ever be judged, and every one would read Healthy.
        var windowsBaselines = await _windowsPatchBaselineRepository.GetAllAsync(cancellationToken);

        return hosts
            .Select(host =>
            {
                var (agentState, latestAgent) = AgentVersionDrift.Evaluate(host, latestByPlatform, now);
                return HostDto.FromEntity(
                    host,
                    appUpdateCounts.GetValueOrDefault(host.Id),
                    unpatched.GetValueOrDefault(host.Id)?.Count ?? 0,
                    patched.GetValueOrDefault(host.Id)?.Count ?? 0,
                    latestAgent,
                    agentState,
                    failedByHost.GetValueOrDefault(host.Id),
                    unstableHosts.Contains(host.Id),
                    windowsBaselines);
            })
            .ToList();
    }

    private static IReadOnlyDictionary<Guid, int> BuildAppUpdateCountsByHost(
        IReadOnlyList<InstallationPatchStatus> patchStatuses)
    {
        var counts = new Dictionary<Guid, int>();
        foreach (var status in patchStatuses.Where(s => s.UpdateAvailable == true))
        {
            counts[status.HostId] = counts.GetValueOrDefault(status.HostId) + 1;
        }

        return counts;
    }
}
