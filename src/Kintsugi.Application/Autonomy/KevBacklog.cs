using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Application.Hosts;
using Kintsugi.Domain.Entities;

namespace Kintsugi.Application.Autonomy;

/// <summary>One day of the KEV backlog, worked out or stored.</summary>
public record KevBacklogPoint(
    int DevicesOpen,
    int WithinTarget,
    int Overdue,
    int DueWithin7Days,
    int OverdueNotSeen7Days,
    string? TopCveId,
    int TopCveDevices);

/// <summary>
/// The KEV backlog: devices that still have a Known Exploited Vulnerability open, split by whether CISA's own due
/// date for that CVE has passed. One rule, here, for the daily record and the live "today" point.
/// </summary>
/// <remarks>
/// <para>
/// <b>The target is 14 days from the day CISA added the CVE to its list</b> (<see cref="Vulnerability.KevDateAddedUtc"/>),
/// the "fixed within 14 days" the Horizon report states. It is not CISA's own due date, which differs per entry. A device
/// is overdue when any open exploited CVE on it is past that, and it is counted once however many it has. The clock is the
/// CVE's, not the device's: Kintsugi does not record when a CVE first became open on a device, so a CVE added long ago
/// counts as overdue on a device that only just acquired it. A CVE with no added date never makes a device overdue.
/// </para>
/// <para>
/// "Open" is the Hosts list's own unpatched side (<see cref="HostCveExposure"/>), so it covers only what CVE
/// mapping has assessed. A device whose software was never mapped is not counted, so these figures can sit below
/// another tool's.
/// </para>
/// </remarks>
public static class KevBacklog
{
    /// <summary>Days from the day CISA added a CVE until a device that still has it open is overdue.</summary>
    public const int TargetDays = 14;

    public const int DueSoonDays = 7;
    public const int NotSeenDays = 7;

    public static KevBacklogPoint Compute(
        IReadOnlyList<Host> hosts,
        IReadOnlyDictionary<Guid, HashSet<string>> unpatchedByHost,
        IReadOnlyList<Vulnerability> exploited,
        DateTimeOffset now)
    {
        var dueByCve = exploited.ToDictionary(
            v => v.CveId,
            v => v.KevDateAddedUtc is { } added ? added.UtcDateTime.Date.AddDays(TargetDays) : (DateTime?)null,
            StringComparer.OrdinalIgnoreCase);
        var today = now.UtcDateTime.Date;
        var devicesByCve = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        int open = 0, overdue = 0, dueSoon = 0, notSeen = 0;

        foreach (var host in hosts)
        {
            if (!unpatchedByHost.TryGetValue(host.Id, out var cves))
            {
                continue;
            }

            DateTime? earliestDue = null;
            var any = false;
            foreach (var cve in cves)
            {
                if (!dueByCve.TryGetValue(cve, out var due))
                {
                    continue;   // not on the exploited list
                }

                any = true;
                devicesByCve[cve] = devicesByCve.GetValueOrDefault(cve) + 1;
                if (due is { } d && (earliestDue is null || d < earliestDue))
                {
                    earliestDue = d;
                }
            }

            if (!any)
            {
                continue;
            }

            open++;
            if (earliestDue is { } e && e < today)
            {
                overdue++;
                if (host.LastSeenUtc is null || now - host.LastSeenUtc.Value >= TimeSpan.FromDays(NotSeenDays))
                {
                    notSeen++;
                }
            }
            else if (earliestDue is { } soon && soon <= today.AddDays(DueSoonDays))
            {
                dueSoon++;
            }
        }

        var top = devicesByCve.OrderByDescending(p => p.Value).ThenBy(p => p.Key, StringComparer.Ordinal).Select(p => (KeyValuePair<string, int>?)p).FirstOrDefault();
        return new KevBacklogPoint(open, open - overdue, overdue, dueSoon, notSeen, top?.Key, top?.Value ?? 0);
    }

    /// <summary>Reads what the Hosts list reads and works out today's point.</summary>
    public static async Task<KevBacklogPoint> LoadAsync(
        IHostRepository hostRepository,
        IUpgradePathRepository upgradePaths,
        IVulnerabilityRepository vulnerabilities,
        IInstalledPackageRepository installedPackages,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var hosts = await hostRepository.GetAllAsync(cancellationToken);
        var unpatched = await HostCveExposure.LoadUnpatchedAsync(hosts, upgradePaths, vulnerabilities, installedPackages, cancellationToken);
        var exploited = await vulnerabilities.GetKnownExploitedAsync(cancellationToken);
        return Compute(hosts, unpatched, exploited, now);
    }
}
