using MediatR;
using Kintsugi.Application.Common.Exceptions;
using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Domain.Entities;
using Kintsugi.Domain.Enums;

namespace Kintsugi.Application.Baselines.Commands.ReportBaselineResults;

public class ReportBaselineResultsCommandHandler : IRequestHandler<ReportBaselineResultsCommand>
{
    /// <summary>How recently a person must have been resolved on a host for a result about them to be
    /// accepted from it. A day: long enough for a report that follows the hourly people call, short enough
    /// that a person who has left a PC cannot go on being reported on from it.</summary>
    public static readonly TimeSpan SubjectWindow = TimeSpan.FromHours(24);

    private readonly IBaselineRepository _baselines;
    private readonly IHostRepository _hosts;
    private readonly IInstalledApplicationRepository _applications;
    private readonly IUnitOfWork _unitOfWork;

    public ReportBaselineResultsCommandHandler(
        IBaselineRepository baselines, IHostRepository hosts, IInstalledApplicationRepository applications, IUnitOfWork unitOfWork)
    {
        _baselines = baselines;
        _hosts = hosts;
        _applications = applications;
        _unitOfWork = unitOfWork;
    }

    public async Task Handle(ReportBaselineResultsCommand request, CancellationToken cancellationToken)
    {
        var host = await _hosts.GetBySerialNumberAsync(request.SerialNumber, cancellationToken);
        if (host is null || host.DeletedAtUtc is not null)
        {
            throw new NotFoundException($"No host is registered with serial number '{request.SerialNumber}'.");
        }

        var now = DateTimeOffset.UtcNow;
        var assigned = BaselineCompliance.AssignedTo(
                await _baselines.GetAllAsync(cancellationToken), host, await _applications.GetNamesForHostAsync(host.Id, cancellationToken))
            .ToDictionary(b => b.Id);
        var facts = (await _baselines.GetHostFactsAsync(host.Id, cancellationToken)).ToDictionary(f => f.Key, StringComparer.Ordinal);
        var served = (await _baselines.GetServedVersionsAsync(assigned.Keys.ToList(), cancellationToken)).ToDictionary(v => v.Id);

        // The people this host may report on: resolved here by the server, recently. The agent's word for who
        // was signed in is never enough on its own — see HostPersonResolution.
        var resolvedSubjects = (await _baselines.GetResolutionsAsync(host.Id, cancellationToken))
            .Where(r => r.Email is not null && r.LastSeenUtc >= now - SubjectWindow)
            .Select(r => r.Email!)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var result in request.Baselines)
        {
            // Only the version currently served for a baseline this host is assigned is recorded. Anything
            // else is dropped rather than failing the whole report, which would cost the other baselines'
            // results: a report that raced an unassignment, a version the server never served — or an
            // older signed one, which an agent sends when its fetch failed and it fell back to its cached
            // copy (baseline::refresh). Accepting that one would delete the current version's rows below.
            if (!served.TryGetValue(result.VersionId, out var version))
            {
                continue;
            }

            var items = BaselineDocument.Parse(version.Document).Items;
            var machineKeys = items.Where(i => i.IsMachineJudged).Select(i => i.Key).ToHashSet(StringComparer.Ordinal);
            var personKeys = items.Where(i => i.IsPerPerson).Select(i => i.Key).ToHashSet(StringComparer.Ordinal);
            var factKeys = items.Where(i => i.IsFact).Select(i => i.Key).ToHashSet(StringComparer.Ordinal);

            var allVersionIds = await _baselines.GetVersionIdsAsync(version.BaselineId, cancellationToken);
            var existing = await _baselines.GetHostStatesAsync(host.Id, allVersionIds, cancellationToken);

            // A host on a new version has nothing to say about the old one any more.
            _baselines.RemoveStates(existing.Where(s => s.BaselineVersionId != version.Id));

            var current = existing.Where(s => s.BaselineVersionId == version.Id).ToDictionary(s => (s.ItemKey, s.Subject));
            foreach (var row in current.Values)
            {
                row.MarkChecked(now);
            }

            foreach (var item in result.Items)
            {
                var subject = string.IsNullOrWhiteSpace(item.Subject)
                    ? HostBaselineItemState.MachineSubject
                    : PersonSignature.NormalizeEmail(item.Subject);

                // A host's own result: any judged item. A per-person item takes one too — it is how an agent
                // too old to know the kind reports it, as an error, until it updates.
                // A person's result: only a per-person item, and only a person resolved here recently.
                var accepted = subject == HostBaselineItemState.MachineSubject
                    ? machineKeys.Contains(item.Key) || personKeys.Contains(item.Key)
                    : personKeys.Contains(item.Key) && resolvedSubjects.Contains(subject);
                if (!accepted)
                {
                    continue;
                }

                if (!current.TryGetValue((item.Key, subject), out var row))
                {
                    row = HostBaselineItemState.Create(host.Id, version.Id, item.Key, now, subject);
                    current[(item.Key, subject)] = row;
                    await _baselines.AddStateAsync(row, cancellationToken);
                }

                row.Record(Enum.Parse<BaselineItemState>(item.State), item.Observed, item.Detail, now);
            }

            foreach (var key in factKeys)
            {
                if (facts.TryGetValue(key, out var known))
                {
                    known.MarkChecked(now);
                }
            }

            foreach (var reported in result.Facts.Where(f => factKeys.Contains(f.Key)))
            {
                if (facts.TryGetValue(reported.Key, out var fact))
                {
                    fact.Record(reported.Value, now);
                }
                else
                {
                    fact = HostFact.Create(host.Id, reported.Key, reported.Value, now);
                    facts[reported.Key] = fact;
                    await _baselines.AddFactAsync(fact, cancellationToken);
                }
            }
        }

        await _unitOfWork.SaveChangesAsync(cancellationToken);
    }
}
