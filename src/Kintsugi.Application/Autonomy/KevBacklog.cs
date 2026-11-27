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
/// <b>The target is CISA's due date for each CVE</b> (<see cref="Vulnerability.KevDueDateUtc"/>), not a number of
/// days of ours. A device is overdue when any open exploited CVE on it is past its due date, and it is counted once
/// however many it has. A CVE with no due date can never make a device overdue; CISA gives every entry one, so that
/// only happens for a catalogue row that has not been refreshed.
/// </para>
/// <para>
/// "Open" is the Hosts list's own unpatched side (<see cref="HostCveExposure"/>), so it covers only what CVE
/// mapping has assessed. A device whose software was never mapped is not counted, so these figures can sit below
/// another tool's.
/// </para>
/// </remarks>
public static class KevBacklog
{
    public const int DueSoonDays = 7;
    public const int NotSeenDays = 7;

    public static KevBacklogPoint Compute(
        IReadOnlyList<Host> hosts,
        IReadOnlyDictionary<Guid, HashSet<string>> unpatchedByHost,
        IReadOnlyList<Vulnerability> exploited,
        DateTimeOffset now)
    {
        var dueByCve = exploited.ToDictionary(v => v.CveId, v => v.KevDueDateUtc, StringComparer.OrdinalIgnoreCase);
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
                if (due is { } d && (earliestDue is null || d.UtcDateTime.Date < earliestDue))
                {
                    earliestDue = d.UtcDateTime.Date;
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
