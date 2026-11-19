using MediatR;
using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Domain.Entities;
using Kintsugi.Domain.Enums;

namespace Kintsugi.Application.SoftwareTitles.Queries.GetSoftware;

public class GetSoftwareQueryHandler : IRequestHandler<GetSoftwareQuery, SoftwareScreenDto>
{
    private readonly ISoftwareTitleRepository _titles;
    private readonly IScriptSecretRepository _secrets;
    private readonly IHostRepository _hosts;
    private readonly IInstalledApplicationRepository _installedApplications;
    private readonly IPatchFailureRepository _patchFailures;
    private readonly IUpgradePathRepository _upgradePaths;

    public GetSoftwareQueryHandler(
        ISoftwareTitleRepository titles,
        IScriptSecretRepository secrets,
        IHostRepository hosts,
        IInstalledApplicationRepository installedApplications,
        IPatchFailureRepository patchFailures,
        IUpgradePathRepository upgradePaths)
    {
        _titles = titles;
        _secrets = secrets;
        _hosts = hosts;
        _installedApplications = installedApplications;
        _patchFailures = patchFailures;
        _upgradePaths = upgradePaths;
    }

    public async Task<SoftwareScreenDto> Handle(GetSoftwareQuery request, CancellationToken cancellationToken)
    {
        var titles = await _titles.GetAllAsync(cancellationToken);
        var secrets = (await _secrets.GetAllAsync(cancellationToken))
            .Select(s => new ScriptSecretSummaryDto(s.Name, !string.IsNullOrEmpty(s.Value), s.UpdatedBy, s.UpdatedUtc))
            .ToList();

        if (titles.Count == 0)
        {
            return new SoftwareScreenDto(Array.Empty<SoftwareTitleDto>(), secrets);
        }

        var fleet = await SoftwareFleet.LoadAsync(_hosts, _installedApplications, _patchFailures, cancellationToken);
        var scripts = new SoftwareTitleScripts(_upgradePaths);

        var dtos = new List<SoftwareTitleDto>();
        foreach (var title in titles)
        {
            var counts = fleet.States(title).GroupBy(s => s.State).ToDictionary(g => g.Key, g => g.Count());
            var script = await scripts.ResolveAsync(title, cancellationToken);

            dtos.Add(new SoftwareTitleDto(
                title.Id,
                title.Name,
                title.OperatingSystem,
                title.PackageManager,
                title.PackageId,
                title.DetectAs,
                title.Mode,
                title.SecretNames,
                title.AssignedToAllHosts,
                fleet.HostNamesOf(title.AssignedHostIds),
                counts.GetValueOrDefault(SoftwareHostState.Compliant),
                counts.GetValueOrDefault(SoftwareHostState.Pending),
                counts.GetValueOrDefault(SoftwareHostState.Failed),
                counts.GetValueOrDefault(SoftwareHostState.NotApplicable),
                script.State));
        }

        return new SoftwareScreenDto(dtos, secrets);
    }
}

/// <summary>
/// The fleet as the Software screen and Run now read it: every host, its inventory, and which (host,
/// application) pairs have an outstanding failure. Loaded once per call and shared by every title.
/// </summary>
internal sealed class SoftwareFleet
{
    private readonly IReadOnlyList<Host> _hosts;
    private readonly IReadOnlyDictionary<Guid, IReadOnlyCollection<InstalledApplication>> _inventory;
    private readonly HashSet<(Guid HostId, string Name)> _failures;

    private SoftwareFleet(
        IReadOnlyList<Host> hosts,
        IReadOnlyDictionary<Guid, IReadOnlyCollection<InstalledApplication>> inventory,
        HashSet<(Guid, string)> failures)
    {
        _hosts = hosts;
        _inventory = inventory;
        _failures = failures;
    }

    public static async Task<SoftwareFleet> LoadAsync(
        IHostRepository hosts, IInstalledApplicationRepository installed, IPatchFailureRepository failures, CancellationToken cancellationToken)
    {
        // GetAllAsync leaves out a host whose removal has been requested: it is on its way out and is
        // neither pending nor a host Run now should name.
        var allHosts = (await hosts.GetAllAsync(cancellationToken)).Where(h => h.DeletedAtUtc is null).ToList();
        var inventory = (await installed.GetAllAsync(cancellationToken))
            .GroupBy(a => a.HostId)
            .ToDictionary(g => g.Key, g => (IReadOnlyCollection<InstalledApplication>)g.ToList());
        var outstanding = (await failures.GetAllAsync(cancellationToken))
            .Where(f => f.Resolution == PatchFailureResolution.Outstanding)
            .Select(f => (f.HostId, f.ApplicationName.ToLowerInvariant()))
            .ToHashSet();

        return new SoftwareFleet(allHosts, inventory, outstanding);
    }

    public IEnumerable<(Host Host, SoftwareHostState State)> States(SoftwareTitle title)
    {
        var failureName = title.Name.ToLowerInvariant();
        foreach (var host in _hosts)
        {
            var inventory = _inventory.GetValueOrDefault(host.Id) ?? Array.Empty<InstalledApplication>();
            var state = SoftwareCompliance.StateOf(title, host, inventory, _failures.Contains((host.Id, failureName)));
            if (state != SoftwareHostState.NotAssigned)
            {
                yield return (host, state);
            }
        }
    }

    /// <summary>The names of the assigned hosts that still exist, sorted; an id whose host has gone is
    /// left out, as it matches nothing.</summary>
    public IReadOnlyList<string> HostNamesOf(IEnumerable<Guid> hostIds)
    {
        var ids = hostIds.ToHashSet();
        return _hosts.Where(h => ids.Contains(h.Id)).Select(h => h.Hostname).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
    }
}
