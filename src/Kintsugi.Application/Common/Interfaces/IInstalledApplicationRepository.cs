using Kintsugi.Application.Applications;
using Kintsugi.Application.UpgradePaths;
using Kintsugi.Domain.Entities;

namespace Kintsugi.Application.Common.Interfaces;

public interface IInstalledApplicationRepository
{
    Task<IReadOnlyList<InstalledApplication>> GetByHostIdAsync(Guid hostId, CancellationToken cancellationToken);

    /// <summary>The one application (by exact name) a host has reported installed, if any —
    /// used to apply a targeted update (e.g. <see cref="Kintsugi.Domain.Entities.InstalledApplication.UpdateVersion"/>)
    /// without pulling that host's whole inventory into memory.</summary>
    Task<InstalledApplication?> GetByHostIdAndNameAsync(Guid hostId, string name, CancellationToken cancellationToken);

    Task AddRangeAsync(IEnumerable<InstalledApplication> applications, CancellationToken cancellationToken);

    /// <summary>The names of every application one host reported, and nothing else — what a baseline's
    /// "hosts with application X installed" target is checked against on each check-in.</summary>
    Task<IReadOnlyList<string>> GetNamesForHostAsync(Guid hostId, CancellationToken cancellationToken);

    /// <summary>The hosts that reported an application named exactly <paramref name="name"/>, ignoring case.</summary>
    Task<IReadOnlySet<Guid>> GetHostIdsWithApplicationAsync(string name, CancellationToken cancellationToken);

    /// <summary>Up to <paramref name="limit"/> distinct application names containing <paramref name="query"/>
    /// (ignoring case; every name when it is blank), alphabetically — the assignment picker's suggestions.</summary>
    Task<IReadOnlyList<string>> SearchNamesAsync(string? query, int limit, CancellationToken cancellationToken);
    void RemoveRange(IEnumerable<InstalledApplication> applications);

    /// <summary>Application name paired with the count of distinct hosts reporting it installed.</summary>
    Task<IReadOnlyList<ApplicationSummaryDto>> GetSummariesAsync(CancellationToken cancellationToken);

    /// <summary>Every distinct (application, version, OS, parent-application) combination seen
    /// across the whole fleet, for upgrade-path research — grouped at the database level, so this
    /// stays cheap however many hosts actually exist.</summary>
    Task<IReadOnlyList<ApplicationVersionVariantDto>> GetApplicationVersionVariantsAsync(CancellationToken cancellationToken);
}
