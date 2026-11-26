using MediatR;
using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Application.ManagedApplications;
using Kintsugi.Domain.Enums;

namespace Kintsugi.Application.Vulnerabilities.Queries.GetExposedApplications;

public class GetExposedApplicationsQueryHandler : IRequestHandler<GetExposedApplicationsQuery, ExposedApplicationsDto>
{
    private readonly IVulnerabilityRepository _vulnerabilities;
    private readonly IManagedApplicationRepository _managed;
    private readonly IUpgradePathRepository _upgradePaths;

    public GetExposedApplicationsQueryHandler(
        IVulnerabilityRepository vulnerabilities,
        IManagedApplicationRepository managed,
        IUpgradePathRepository upgradePaths)
    {
        _vulnerabilities = vulnerabilities;
        _managed = managed;
        _upgradePaths = upgradePaths;
    }

    public async Task<ExposedApplicationsDto> Handle(GetExposedApplicationsQuery request, CancellationToken cancellationToken)
    {
        // Every finding, not only the exploited ones: an application nobody patches is exposed whatever its score.
        var findings = await _vulnerabilities.GetFindingsAsync(false, cancellationToken);
        var installed = (await _vulnerabilities.GetApplicationInstallationCountsAsync(cancellationToken))
            .ToDictionary(c => (c.SubjectKey, c.Version), c => c.HostCount);
        var managed = await _managed.GetAllAsync(cancellationToken);

        // What research said about each name. Found means Kintsugi's own patching deals with it, so it is not here.
        var statuses = (await _upgradePaths.GetSummariesAsync(cancellationToken))
            .GroupBy(p => p.ApplicationName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Select(p => p.Status).ToList(), StringComparer.OrdinalIgnoreCase);

        var byApplication = new Dictionary<string, Exposure>(StringComparer.Ordinal);
        foreach (var finding in findings)
        {
            foreach (var subject in finding.Subjects.Where(s => s.SubjectKind == CpeSubjectKind.Application))
            {
                if (!byApplication.TryGetValue(subject.SubjectKey, out var exposure))
                {
                    byApplication[subject.SubjectKey] = exposure = new Exposure(subject.DisplayName);
                }

                exposure.Add(finding, subject.Version);
            }
        }

        var exposed = new List<ExposedApplicationDto>();
        foreach (var (key, exposure) in byApplication)
        {
            var row = ManagedApplicationMatcher.Find(managed, exposure.DisplayName);
            ExposureRoute route;
            if (row is not null)
            {
                route = ExposureRoute.ManagedElsewhere;
            }
            else if (statuses.TryGetValue(exposure.DisplayName, out var found)
                && !found.Contains(UpgradePathStatus.Found)
                && found.Any(s => s is UpgradePathStatus.NotFound or UpgradePathStatus.Failed))
            {
                route = ExposureRoute.NoUpgradePath;
            }
            else
            {
                // Found, or not researched yet: the normal pipeline owns it.
                continue;
            }

            exposed.Add(new ExposedApplicationDto(
                exposure.DisplayName,
                route,
                row?.Owner,
                row?.Source,
                ExposureRules.Criticality(exposure.KnownExploited, exposure.WorstScore),
                exposure.WorstScore,
                exposure.WorstCveId,
                exposure.CveIds.Count,
                exposure.KnownExploited,
                exposure.KnownRansomwareUse,
                exposure.Versions.Sum(v => installed.GetValueOrDefault((key, v)))));
        }

        return new ExposedApplicationsDto(exposed
            .OrderBy(a => a.Criticality)
            // No owner and no route is worse than a named owner at the same urgency.
            .ThenBy(a => a.Route == ExposureRoute.NoUpgradePath ? 0 : 1)
            .ThenByDescending(a => a.HostCount)
            .ThenBy(a => a.ApplicationName, StringComparer.OrdinalIgnoreCase)
            .ToList());
    }

    private sealed class Exposure
    {
        public Exposure(string displayName) => DisplayName = displayName;

        public string DisplayName { get; }
        public HashSet<string> CveIds { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> Versions { get; } = new(StringComparer.Ordinal);
        public double? WorstScore { get; private set; }
        public string? WorstCveId { get; private set; }
        public bool KnownExploited { get; private set; }
        public bool KnownRansomwareUse { get; private set; }

        private (int Exploited, double Score) _worstRank = (-1, -1);

        public void Add(VulnerabilityFindingRow finding, string version)
        {
            CveIds.Add(finding.CveId);
            Versions.Add(version);
            KnownExploited |= finding.KnownExploited;
            KnownRansomwareUse |= finding.KevKnownRansomwareUse;

            if (finding.CvssBaseScore is { } score && (WorstScore is null || score > WorstScore))
            {
                WorstScore = score;
            }

            // The CVE to quote to a vendor: the exploited one first, then the highest score.
            var rank = (finding.KnownExploited ? 1 : 0, finding.CvssBaseScore ?? -1);
            if (rank.CompareTo(_worstRank) > 0)
            {
                WorstCveId = finding.CveId;
                _worstRank = rank;
            }
        }
    }
}

/// <summary>How urgent an exposed application is. Rules, not a model: a person reading the list can see why a row is where it is.</summary>
public static class ExposureRules
{
    public static ExposureCriticality Criticality(bool knownExploited, double? worstScore) =>
        // CISA lists it as exploited in the wild: nothing else about it matters more.
        knownExploited ? ExposureCriticality.Critical
        : worstScore is null ? ExposureCriticality.Unscored
        : worstScore >= 9.0 ? ExposureCriticality.Critical
        : worstScore >= 7.0 ? ExposureCriticality.High
        : worstScore >= 4.0 ? ExposureCriticality.Medium
        : ExposureCriticality.Low;
}
