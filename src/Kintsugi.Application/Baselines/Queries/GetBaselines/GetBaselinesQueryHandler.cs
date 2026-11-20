using MediatR;
using Kintsugi.Application.Common.Interfaces;

namespace Kintsugi.Application.Baselines.Queries.GetBaselines;

public class GetBaselinesQueryHandler : IRequestHandler<GetBaselinesQuery, IReadOnlyList<BaselineSummaryDto>>
{
    private readonly IBaselineRepository _baselines;
    private readonly IHostRepository _hosts;
    private readonly IInstalledApplicationRepository _applications;

    public GetBaselinesQueryHandler(IBaselineRepository baselines, IHostRepository hosts, IInstalledApplicationRepository applications)
    {
        _baselines = baselines;
        _hosts = hosts;
        _applications = applications;
    }

    public async Task<IReadOnlyList<BaselineSummaryDto>> Handle(GetBaselinesQuery request, CancellationToken cancellationToken)
    {
        var baselines = await _baselines.GetAllAsync(cancellationToken);
        if (baselines.Count == 0)
        {
            return Array.Empty<BaselineSummaryDto>();
        }

        var versions = await _baselines.GetVersionsAsync(baselines.Select(b => b.Id).ToList(), cancellationToken);
        var hosts = await _hosts.GetAllAsync(cancellationToken);
        var served = baselines
            .Select(b => (Baseline: b, Version: BaselineCompliance.ServedVersion(versions.Where(v => v.BaselineId == b.Id))))
            .ToList();
        var states = await _baselines.GetStatesAsync(
            served.Where(s => s.Version is not null).Select(s => s.Version!.Id).ToList(), cancellationToken);
        var counted = await _baselines.GetCountedSubjectsAsync(states.Select(s => s.HostId).Distinct().ToList(), cancellationToken);

        var summaries = new List<BaselineSummaryDto>();
        foreach (var s in served)
        {
            var withApplication = await BaselineCompliance.HostsWithApplicationAsync(s.Baseline, _applications, cancellationToken);
            var applicable = BaselineCompliance.ApplicableHosts(s.Baseline, hosts, withApplication);
            var items = s.Version is null ? Array.Empty<BaselineItem>() : BaselineDocument.Parse(s.Version.Document).Items;
            var perItem = BaselineCompliance.PerItem(
                items, applicable.Select(h => h.Id).ToList(), states.Where(st => st.BaselineVersionId == s.Version?.Id), counted);

            summaries.Add(new BaselineSummaryDto(
                s.Baseline.Id,
                s.Baseline.Name,
                versions.Where(v => v.BaselineId == s.Baseline.Id).Select(v => v.Number).DefaultIfEmpty(0).Max(),
                s.Version?.Number,
                s.Baseline.AssignedToAllWindowsHosts,
                applicable.Count,
                items.Count(i => i.IsJudged),
                BaselineCompliance.Sum(perItem.Where(i => i.Item.IsJudged).Select(i => i.Counts)),
                s.Baseline.AssignedApplicationName));
        }

        return summaries.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }
}
