using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Application.UpgradePaths;
using Kintsugi.Domain.Entities;
using Kintsugi.Domain.Enums;

namespace Kintsugi.Application.Baselines;

/// <summary>
/// The compliance numbers: deterministic counting over what hosts reported, with no model involved
/// (the design keeps "the compliance numbers" on the deterministic side on purpose).
/// </summary>
public static class BaselineCompliance
{
    /// <summary>The version agents are given: the highest-numbered <em>signed</em> one. A newer unsigned
    /// draft never displaces it.</summary>
    public static BaselineVersion? ServedVersion(IEnumerable<BaselineVersion> versions) =>
        versions.Where(v => v.IsSigned).MaxBy(v => v.Number);

    /// <summary>The live hosts <paramref name="baseline"/> applies to. <paramref name="hostsWithApplication"/>
    /// is the hosts whose inventory holds the baseline's <c>AssignedApplicationName</c>
    /// (<c>IInstalledApplicationRepository.GetHostIdsWithApplicationAsync</c>); empty or null when it has none.</summary>
    public static IReadOnlyList<Host> ApplicableHosts(Baseline baseline, IEnumerable<Host> hosts, IReadOnlySet<Guid>? hostsWithApplication = null) =>
        hosts.Where(h => h.DeletedAtUtc is null
                && baseline.AppliesTo(h.Id, IsWindows(h), hostsWithApplication?.Contains(h.Id) == true))
            .ToList();

    public static bool IsWindows(Host host) => PlatformBucket.From(host.OperatingSystem) == PlatformBucket.Windows;

    /// <summary>The hosts holding <paramref name="baseline"/>'s assigned application, or an empty set when it
    /// names none — the second argument <see cref="ApplicableHosts"/> wants.</summary>
    public static async Task<IReadOnlySet<Guid>> HostsWithApplicationAsync(
        Baseline baseline, IInstalledApplicationRepository applications, CancellationToken cancellationToken) =>
        baseline.AssignedApplicationName is null
            ? new HashSet<Guid>()
            : await applications.GetHostIdsWithApplicationAsync(baseline.AssignedApplicationName, cancellationToken);

    /// <summary>The baselines assigned to one host, given the names of the applications it reported. What
    /// both agent routes and the people route use.</summary>
    public static IReadOnlyList<Baseline> AssignedTo(IEnumerable<Baseline> baselines, Host host, IReadOnlyCollection<string> installedApplicationNames) =>
        baselines.Where(b => b.AppliesTo(host.Id, IsWindows(host), b.IsAssignedApplication(installedApplicationNames))).ToList();

    /// <summary>
    /// How bad a state is, for "a host's state for a per-person item is the worst of its people's": Error,
    /// then Drifted, then Compliant, then NotApplicable.
    /// </summary>
    public static int Severity(BaselineItemState state) => state switch
    {
        BaselineItemState.Error => 4,
        BaselineItemState.Drifted => 3,
        BaselineItemState.Compliant => 2,
        _ => 1
    };

    /// <summary>
    /// One row per (host, judged item): what that host counts as for the item.
    /// </summary>
    /// <remarks>
    /// A machine item's row is its <see cref="HostBaselineItemState.MachineSubject"/> row. A per-person item's
    /// is the worst of the host's people's rows — counting only a person in <paramref name="countedSubjects"/>,
    /// that is still resolved on that host and still holding a copy (<c>IBaselineRepository.GetCountedSubjectsAsync</c>)
    /// — and, when no such person has a row, the host's own subject-less row: "nobody with a signature signed
    /// in", written by <c>POST /api/baseline-people</c>, or an error from an agent too old to know the kind.
    /// A host with neither has not reported the item.
    /// </remarks>
    public static IReadOnlyList<HostBaselineItemState> Effective(
        IReadOnlyList<BaselineItem> items,
        IEnumerable<HostBaselineItemState> states,
        IReadOnlySet<(Guid HostId, string Email)>? countedSubjects = null)
    {
        var machineKeys = items.Where(i => i.IsSetting).Select(i => i.Key).ToHashSet(StringComparer.Ordinal);
        var personKeys = items.Where(i => i.IsPerPerson).Select(i => i.Key).ToHashSet(StringComparer.Ordinal);
        var counted = countedSubjects ?? new HashSet<(Guid, string)>();
        var all = states.ToList();

        var effective = all
            .Where(s => machineKeys.Contains(s.ItemKey) && s.Subject == HostBaselineItemState.MachineSubject)
            .ToList();

        foreach (var group in all.Where(s => personKeys.Contains(s.ItemKey)).GroupBy(s => (s.HostId, s.ItemKey)))
        {
            var worst = group
                .Where(s => s.Subject != HostBaselineItemState.MachineSubject && counted.Contains((s.HostId, s.Subject)))
                .MaxBy(s => Severity(s.State));
            var row = worst ?? group.FirstOrDefault(s => s.Subject == HostBaselineItemState.MachineSubject);
            if (row is not null)
            {
                effective.Add(row);
            }
        }

        return effective;
    }

    /// <summary>
    /// Per item, how many of <paramref name="applicableHostIds"/> are in each state for it. Only rows for
    /// those hosts count — a host taken off the baseline drops out of the numbers rather than being
    /// counted for ever on its last report — and each host counts once per item (see <see cref="Effective"/>
    /// for how a per-person item's people become one state).
    /// </summary>
    public static IReadOnlyList<BaselineItemComplianceDto> PerItem(
        IReadOnlyList<BaselineItem> items,
        IReadOnlyCollection<Guid> applicableHostIds,
        IEnumerable<HostBaselineItemState> states,
        IReadOnlySet<(Guid HostId, string Email)>? countedSubjects = null)
    {
        var hostIds = applicableHostIds.ToHashSet();
        var byKey = Effective(items, states.Where(s => hostIds.Contains(s.HostId)), countedSubjects)
            .GroupBy(s => s.ItemKey, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

        return items.Select(item =>
        {
            if (!item.IsJudged)
            {
                return new BaselineItemComplianceDto(item, new ComplianceCountsDto(0, 0, 0, 0, 0));
            }

            var reported = byKey.TryGetValue(item.Key, out var rows) ? rows : new List<HostBaselineItemState>();
            return new BaselineItemComplianceDto(item, Count(reported.Select(r => r.State), hostIds.Count));
        }).ToList();
    }

    /// <summary>Per host, how many of the version's judged items are in each state.</summary>
    public static IReadOnlyList<BaselineHostComplianceDto> PerHost(
        IReadOnlyList<BaselineItem> items,
        IReadOnlyList<Host> applicableHosts,
        IEnumerable<HostBaselineItemState> states,
        IReadOnlySet<(Guid HostId, string Email)>? countedSubjects = null)
    {
        var judgedCount = items.Count(i => i.IsJudged);
        var byHost = Effective(items, states, countedSubjects)
            .GroupBy(s => s.HostId)
            .ToDictionary(g => g.Key, g => g.ToList());

        return applicableHosts
            .Select(host =>
            {
                var rows = byHost.TryGetValue(host.Id, out var found) ? found : new List<HostBaselineItemState>();
                return new BaselineHostComplianceDto(
                    host.Id,
                    host.Hostname,
                    Count(rows.Select(r => r.State), judgedCount),
                    rows.Count == 0 ? null : rows.Max(r => r.LastCheckedUtc));
            })
            .OrderBy(h => h.Hostname, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static ComplianceCountsDto Sum(IEnumerable<ComplianceCountsDto> counts) =>
        counts.Aggregate(
            new ComplianceCountsDto(0, 0, 0, 0, 0),
            (total, c) => new ComplianceCountsDto(
                total.Compliant + c.Compliant,
                total.Drifted + c.Drifted,
                total.NotApplicable + c.NotApplicable,
                total.Error + c.Error,
                total.NotReported + c.NotReported));

    private static ComplianceCountsDto Count(IEnumerable<BaselineItemState> states, int expected)
    {
        var list = states.ToList();
        return new ComplianceCountsDto(
            list.Count(s => s == BaselineItemState.Compliant),
            list.Count(s => s == BaselineItemState.Drifted),
            list.Count(s => s == BaselineItemState.NotApplicable),
            list.Count(s => s == BaselineItemState.Error),
            Math.Max(0, expected - list.Count));
    }
}
