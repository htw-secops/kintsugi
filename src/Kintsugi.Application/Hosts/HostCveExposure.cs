using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Application.UpgradePaths;
using Kintsugi.Application.Vulnerabilities;
using Kintsugi.Domain.Entities;

namespace Kintsugi.Application.Hosts;

/// <summary>
/// Which CVEs each host is still exposed to, split into <b>unpatched</b> and <b>patched</b>. One place, so the
/// Hosts list's counts and the KEV backlog cannot disagree about whether a host is exposed. Moved here from
/// <c>GetHostsQueryHandler</c> unchanged.
/// </summary>
internal static class HostCveExposure
{
    /// <summary>
    /// Folds each host's application CVE matches into the unpatched or patched bucket. Anything
    /// short of a confirmed-current installation counts as unpatched: an installation with no
    /// resolved upgrade path (<see cref="InstallationPatchStatus.UpdateAvailable"/> is null) has
    /// no verdict that it is fixed, so a CVE against it stays live rather than being dropped as a
    /// coverage gap.
    /// </summary>
    internal static void AddApplicationCveCounts(
        IReadOnlyList<InstallationPatchStatus> patchStatuses,
        IReadOnlyList<ApplicationCveMatch> cveMatches,
        Dictionary<Guid, HashSet<string>> unpatched,
        Dictionary<Guid, HashSet<string>> patched)
    {
        var cveIdsBySubject = cveMatches
            .GroupBy(m => (m.SubjectKey, m.Version))
            .ToDictionary(g => g.Key, g => g.Select(m => m.CveId).ToList());

        foreach (var status in patchStatuses)
        {
            if (!cveIdsBySubject.TryGetValue((status.ApplicationName.ToLowerInvariant(), status.Version), out var cveIds))
            {
                continue;
            }

            var bucket = status.UpdateAvailable == false ? patched : unpatched;
            UnionInto(bucket, status.HostId, cveIds);
        }
    }

    /// <summary>
    /// The operating-system sibling of <see cref="AddApplicationCveCounts"/>. There is no
    /// per-installation verdict for an OS the way <see cref="InstallationPatchStatus"/> gives one
    /// for an application, so the split uses <see cref="Host.OperatingSystemUpdateAvailable"/>
    /// directly — unknown (null, never checked) reads as unpatched, the same rule an unresolved
    /// application path gets, since nothing has confirmed the host is current.
    /// </summary>
    internal static void AddOperatingSystemCveCounts(
        IReadOnlyList<Host> hosts,
        IReadOnlyList<OperatingSystemCveMatch> cveMatches,
        Dictionary<Guid, HashSet<string>> unpatched,
        Dictionary<Guid, HashSet<string>> patched)
    {
        var cveIdsBySubject = cveMatches
            .GroupBy(m => (m.SubjectKey, m.Version))
            .ToDictionary(g => g.Key, g => g.Select(m => m.CveId).ToList());

        foreach (var host in hosts)
        {
            var derived = OperatingSystemSubject.Derive(host);
            if (derived?.Version is null)
            {
                // Never checked in, or reports too little to assess (a Windows build with no
                // update revision, say) — a coverage gap, not a clean bill of health, so it
                // contributes to neither bucket rather than being guessed at.
                continue;
            }

            if (!cveIdsBySubject.TryGetValue((derived.SubjectKey, derived.Version), out var cveIds))
            {
                continue;
            }

            var bucket = host.OperatingSystemUpdateAvailable == false ? patched : unpatched;
            UnionInto(bucket, host.Id, cveIds);
        }
    }

    /// <summary>
    /// The package sibling of <see cref="AddApplicationCveCounts"/>. A distribution package has no
    /// upgrade path of its own — apt, dnf, zypper and pacman are deliberately outside
    /// <c>PackageManagerCatalog</c> (see src/CLAUDE.md) — but the Linux agent resolves its own
    /// pending-update listing down to each installed source package
    /// (<see cref="InstalledPackage.UpdateAvailable"/>), so most packages get an exact,
    /// per-installation verdict rather than one borrowed from the whole host: a host with one
    /// trivial pending package no longer marks every other package's CVEs unpatched. Only when
    /// that verdict is null — an agent predating the field, or pacman/apk, which
    /// <c>scan_os_packages</c> never reports a package from at all — does this fall back to
    /// <see cref="Host.OperatingSystemUpdateAvailable"/>, the same host-wide reasoning this used
    /// unconditionally before per-package data existed: apt/dnf patches the OS *and everything it
    /// shipped* in one pull, so the host's own flag is still the best available answer for a
    /// package that did not say for itself.
    /// </summary>
    internal static async Task AddPackageCveCountsAsync(
        IInstalledPackageRepository installedPackageRepository,
        IReadOnlyList<Host> hosts,
        IReadOnlyList<PackageCveMatch> cveMatches,
        Dictionary<Guid, HashSet<string>> unpatched,
        Dictionary<Guid, HashSet<string>> patched,
        CancellationToken cancellationToken)
    {
        if (cveMatches.Count == 0)
        {
            return;
        }

        var osUpdateAvailableByHost = hosts.ToDictionary(h => h.Id, h => h.OperatingSystemUpdateAvailable);

        var cveIdsByTriple = cveMatches
            .GroupBy(m => new PackageTriple(m.Ecosystem, m.Name, m.Version))
            .ToDictionary(g => g.Key, g => g.Select(m => m.CveId).ToList());

        var names = cveMatches.Select(m => m.Name).Distinct().ToList();
        var versions = cveMatches.Select(m => m.Version).Distinct().ToList();

        var hostPackages = await installedPackageRepository.GetHostPackageTriplesAsync(names, versions, cancellationToken);

        foreach (var hostPackage in hostPackages)
        {
            if (!cveIdsByTriple.TryGetValue(hostPackage.Triple, out var cveIds))
            {
                continue;
            }

            var patchedVerdict = hostPackage.UpdateAvailable == null
                ? osUpdateAvailableByHost.GetValueOrDefault(hostPackage.HostId) == false
                : hostPackage.UpdateAvailable == false;

            UnionInto(patchedVerdict ? patched : unpatched, hostPackage.HostId, cveIds);
        }
    }

    private static void UnionInto(Dictionary<Guid, HashSet<string>> bucket, Guid hostId, IReadOnlyList<string> cveIds)
    {
        if (!bucket.TryGetValue(hostId, out var set))
        {
            set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            bucket[hostId] = set;
        }

        set.UnionWith(cveIds);
    }

    /// <summary>
    /// The CVE ids each host is still exposed to — the same unpatched side the Hosts list counts — loaded the same
    /// way. Anything short of a confirmed-current installation is unpatched (see <see cref="AddApplicationCveCounts"/>).
    /// </summary>
    internal static async Task<IReadOnlyDictionary<Guid, HashSet<string>>> LoadUnpatchedAsync(
        IReadOnlyList<Host> hosts,
        IUpgradePathRepository upgradePaths,
        IVulnerabilityRepository vulnerabilities,
        IInstalledPackageRepository installedPackages,
        CancellationToken cancellationToken)
    {
        var unpatched = new Dictionary<Guid, HashSet<string>>();
        var patched = new Dictionary<Guid, HashSet<string>>();

        AddApplicationCveCounts(
            await upgradePaths.GetInstallationPatchStatusesAsync(cancellationToken),
            await vulnerabilities.GetApplicationCveMatchesAsync(cancellationToken),
            unpatched,
            patched);
        AddOperatingSystemCveCounts(hosts, await vulnerabilities.GetOperatingSystemCveMatchesAsync(cancellationToken), unpatched, patched);
        await AddPackageCveCountsAsync(installedPackages, hosts, await vulnerabilities.GetPackageCveMatchesAsync(cancellationToken), unpatched, patched, cancellationToken);

        return unpatched;
    }
}
