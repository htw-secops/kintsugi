using MediatR;
using Kintsugi.Application.Common.Exceptions;
using Kintsugi.Application.Common.Interfaces;

namespace Kintsugi.Application.Baselines.Queries.GetBaselinePeople;

public class GetBaselinePeopleQueryHandler : IRequestHandler<GetBaselinePeopleQuery, IReadOnlyList<BaselinePersonRowDto>>
{
    private readonly IBaselineRepository _baselines;
    private readonly IHostRepository _hosts;
    private readonly IInstalledApplicationRepository _applications;
    private readonly IPersonSignatureRepository _signatures;

    public GetBaselinePeopleQueryHandler(
        IBaselineRepository baselines, IHostRepository hosts, IInstalledApplicationRepository applications, IPersonSignatureRepository signatures)
    {
        _baselines = baselines;
        _hosts = hosts;
        _applications = applications;
        _signatures = signatures;
    }

    public async Task<IReadOnlyList<BaselinePersonRowDto>> Handle(GetBaselinePeopleQuery request, CancellationToken cancellationToken)
    {
        var baseline = await _baselines.GetAsync(request.BaselineId, cancellationToken)
            ?? throw new NotFoundException($"No baseline with id '{request.BaselineId}'.");

        var served = BaselineCompliance.ServedVersion(await _baselines.GetVersionsAsync(new[] { baseline.Id }, cancellationToken));
        var personItems = served is null
            ? new List<BaselineItem>()
            : BaselineDocument.Parse(served.Document).Items.Where(i => i.IsPerPerson).ToList();
        if (personItems.Count == 0)
        {
            return Array.Empty<BaselinePersonRowDto>();
        }

        var applicable = BaselineCompliance.ApplicableHosts(
                baseline, await _hosts.GetAllAsync(cancellationToken),
                await BaselineCompliance.HostsWithApplicationAsync(baseline, _applications, cancellationToken))
            .ToDictionary(h => h.Id);
        var resolutions = await _baselines.GetResolutionsForHostsAsync(applicable.Keys.ToList(), cancellationToken);
        var copies = (await _signatures.GetByEmailsAsync(
                resolutions.Where(r => r.Email is not null).Select(r => r.Email!).Distinct().ToList(), cancellationToken))
            .ToDictionary(c => c.Email, StringComparer.Ordinal);
        var states = (await _baselines.GetStatesAsync(new[] { served!.Id }, cancellationToken))
            .Where(s => s.Subject.Length > 0)
            .ToDictionary(s => (s.HostId, s.ItemKey, s.Subject));

        return resolutions
            .SelectMany(r => personItems.Select(item =>
            {
                var state = r.Email is not null && states.TryGetValue((r.HostId, item.Key, r.Email), out var s) ? s : null;
                return new BaselinePersonRowDto(
                    r.HostId,
                    applicable[r.HostId].Hostname,
                    r.WindowsUser,
                    r.Email,
                    r.ResolvedBy,
                    r.Reason,
                    r.Email is not null && copies.TryGetValue(r.Email, out var copy) ? copy.RenderedAtUtc : null,
                    item.Key,
                    state?.State,
                    state?.ObservedValue,
                    state?.Detail,
                    state?.LastCheckedUtc,
                    r.LastSeenUtc);
            }))
            .OrderBy(row => row.Hostname, StringComparer.OrdinalIgnoreCase)
            .ThenBy(row => row.WindowsUser, StringComparer.OrdinalIgnoreCase)
            .ThenBy(row => row.ItemKey, StringComparer.Ordinal)
            .ToList();
    }
}
