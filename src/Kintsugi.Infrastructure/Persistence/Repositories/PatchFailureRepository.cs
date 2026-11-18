using Microsoft.EntityFrameworkCore;
using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Application.PatchFailures;
using Kintsugi.Domain.Entities;
using Kintsugi.Domain.Enums;

namespace Kintsugi.Infrastructure.Persistence.Repositories;

public class PatchFailureRepository : IPatchFailureRepository
{
    private readonly ApplicationDbContext _context;

    public PatchFailureRepository(ApplicationDbContext context)
    {
        _context = context;
    }

    public Task<PatchFailure?> GetByIdAsync(Guid id, CancellationToken cancellationToken) =>
        _context.PatchFailures.FirstOrDefaultAsync(f => f.Id == id, cancellationToken);

    // Case-insensitive on ApplicationName, matching how the agents match applications everywhere
    // else (`eq_ignore_ascii_case` in each one's patch cycle) and how UpgradePathRepository looks
    // rows up. A case-sensitive match here would open a second row for the same broken application
    // the day an inventory report settled on different casing.
    public Task<PatchFailure?> GetByHostAndApplicationAsync(Guid hostId, string applicationName, CancellationToken cancellationToken) =>
        _context.PatchFailures
            .Where(f => f.HostId == hostId && f.ApplicationName.ToLower() == applicationName.ToLower())
            // Should two rows exist anyway (opened before this was case-insensitive), the one still
            // outstanding is the one a repeat belongs on.
            .OrderBy(f => f.Resolution == PatchFailureResolution.Outstanding ? 0 : 1)
            .ThenByDescending(f => f.LastFailedUtc)
            .FirstOrDefaultAsync(cancellationToken);

    public async Task<IReadOnlyList<PatchFailure>> GetOutstandingForApplicationAsync(Guid hostId, string applicationName, CancellationToken cancellationToken) =>
        await _context.PatchFailures
            .Where(f => f.HostId == hostId
                && f.ApplicationName.ToLower() == applicationName.ToLower()
                && f.Resolution == PatchFailureResolution.Outstanding)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<PatchFailure>> GetOutstandingForPathAsync(string applicationName, string platform, CancellationToken cancellationToken) =>
        await _context.PatchFailures
            .Where(f => f.ApplicationName.ToLower() == applicationName.ToLower()
                && f.Platform == platform
                && f.Resolution == PatchFailureResolution.Outstanding)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<PatchFailure>> GetOutstandingForHostAsync(Guid hostId, CancellationToken cancellationToken) =>
        await _context.PatchFailures
            .Where(f => f.HostId == hostId && f.Resolution == PatchFailureResolution.Outstanding)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<PatchFailure>> GetAllOutstandingAsync(CancellationToken cancellationToken) =>
        await _context.PatchFailures
            .Where(f => f.Resolution == PatchFailureResolution.Outstanding)
            .ToListAsync(cancellationToken);

    public Task<int> DeleteResolvedBeforeAsync(DateTimeOffset cutoffUtc, CancellationToken cancellationToken) =>
        _context.PatchFailures
            // Only the rows nobody decided on: a success report, or the inventory showing nothing was wrong.
            // Dismissed and ScriptRepaired are an administrator's decision and the evidence that a problem
            // returns, which is what Reopen keeps (its count and first-failed date); deleting them would
            // restart a monthly failure at "failed 1 time since today". There is at most one row per
            // (host, application), so keeping them does not make the table grow without bound.
            .Where(f => (f.Resolution == PatchFailureResolution.PatchSucceeded || f.Resolution == PatchFailureResolution.FalsePositiveAutoResolved)
                && f.ResolvedUtc != null && f.ResolvedUtc < cutoffUtc)
            .ExecuteDeleteAsync(cancellationToken);

    public async Task AddAsync(PatchFailure failure, CancellationToken cancellationToken) =>
        await _context.PatchFailures.AddAsync(failure, cancellationToken);

    public async Task<IReadOnlyList<PatchFailureDto>> GetAllAsync(CancellationToken cancellationToken)
    {
        var rows = await _context.PatchFailures
            .Join(_context.Hosts, f => f.HostId, h => h.Id, (f, h) => new { Failure = f, Host = h })
            // A host pending removal is already hidden from the hosts list, so its failures must not
            // turn up here either — the row would name a machine nothing else in the UI shows, and
            // its fix would be applied to a fleet that no longer includes it.
            .Where(x => x.Host.DeletedAtUtc == null)
            .ToListAsync(cancellationToken);

        // One query for the paths, then matched in memory: the set of distinct (application,
        // platform) pairs among *failing* rows is small, and the alternative is a correlated join on
        // a nullable Platform that EF renders poorly.
        var paths = await _context.UpgradePaths
            .Select(p => new { p.ApplicationName, p.Platform, p.Method, HasScript = p.Script != null, Signed = p.ScriptSignature != null })
            .ToListAsync(cancellationToken);

        var pathsByKey = paths
            .GroupBy(p => (Name: p.ApplicationName.ToLowerInvariant(), p.Platform))
            // Two rows can differ only by the casing of ApplicationName — the DB's uniqueness
            // constraint is case-sensitive while every lookup is not — so take one rather than
            // letting ToDictionary throw. See UpgradePathRepository's own note on this.
            .ToDictionary(g => g.Key, g => g.First());

        // How many *outstanding* rows share each signature: the same failure on many devices is one
        // problem, and the screen says so.
        var outstandingBySignature = rows
            .Where(x => x.Failure.Resolution == PatchFailureResolution.Outstanding && x.Failure.FailureSignature != null)
            .GroupBy(x => x.Failure.FailureSignature!)
            .ToDictionary(g => g.Key, g => g.Count());

        return rows
            .Select(x =>
            {
                var path = x.Failure.Platform is null
                    ? null
                    : pathsByKey.GetValueOrDefault((x.Failure.ApplicationName.ToLowerInvariant(), x.Failure.Platform));

                return new PatchFailureDto(
                    x.Failure.Id,
                    x.Failure.HostId,
                    x.Host.Hostname,
                    x.Host.SerialNumber,
                    x.Failure.ApplicationName,
                    x.Failure.Platform,
                    x.Failure.InstalledVersion,
                    x.Failure.AttemptedVersion,
                    x.Failure.Details,
                    x.Failure.FirstFailedUtc,
                    x.Failure.LastFailedUtc,
                    x.Failure.FailureCount,
                    x.Failure.Resolution,
                    x.Failure.ResolvedUtc,
                    path?.HasScript ?? false,
                    path?.Signed ?? false,
                    path?.Method ?? UpgradeMethod.Unknown,
                    x.Failure.LastVerifiedUtc,
                    x.Failure.FailureCategory,
                    x.Failure.FailureSignature,
                    x.Failure.FailureSignature is { } sig && outstandingBySignature.TryGetValue(sig, out var n) ? n : 1);
            })
            // Outstanding first, then most recent — the screen's default view is the top of this
            // list, so the ordering is what makes it a queue rather than an archive.
            .OrderBy(dto => dto.Resolution == PatchFailureResolution.Outstanding ? 0 : 1)
            .ThenByDescending(dto => dto.LastFailedUtc)
            .ThenBy(dto => dto.ApplicationName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
