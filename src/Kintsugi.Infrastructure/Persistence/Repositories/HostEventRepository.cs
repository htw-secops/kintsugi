using Microsoft.EntityFrameworkCore;
using Kintsugi.Application.Autonomy;
using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Domain.Enums;
using Kintsugi.Domain.Entities;

namespace Kintsugi.Infrastructure.Persistence.Repositories;

public class HostEventRepository : IHostEventRepository
{
    private readonly ApplicationDbContext _context;

    public HostEventRepository(ApplicationDbContext context)
    {
        _context = context;
    }

    public async Task AddAsync(HostEvent hostEvent, CancellationToken cancellationToken) =>
        await _context.HostEvents.AddAsync(hostEvent, cancellationToken);

    public async Task<HostEvent?> GetLatestForSubjectAsync(Guid hostId, string subject, CancellationToken cancellationToken)
    {
        // An event added earlier in this same unit of work is not in the database yet, so the
        // tracked ones are looked at first: two failures reported in one request must fold too.
        var pending = _context.ChangeTracker.Entries<HostEvent>()
            .Where(e => e.State == EntityState.Added && e.Entity.HostId == hostId && e.Entity.Subject == subject)
            .Select(e => e.Entity)
            .OrderByDescending(e => e.OccurredUtc)
            .FirstOrDefault();
        if (pending is not null)
        {
            return pending;
        }

        return await _context.HostEvents
            .Where(e => e.HostId == hostId && e.Subject == subject)
            .OrderByDescending(e => e.OccurredUtc)
            .ThenByDescending(e => e.CreatedAtUtc)
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<HostEvent>> GetForHostAsync(Guid hostId, int take, CancellationToken cancellationToken) =>
        await _context.HostEvents
            .AsNoTracking()
            .Where(e => e.HostId == hostId)
            .OrderByDescending(e => e.OccurredUtc)
            .ThenByDescending(e => e.CreatedAtUtc)
            .Take(take)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<PatchActivityRecord>> GetPatchActivitySinceAsync(DateTimeOffset sinceUtc, CancellationToken cancellationToken) =>
        await _context.HostEvents
            .AsNoTracking()
            .Where(e => e.OccurredUtc >= sinceUtc
                && (e.Kind == HostEventKind.ApplicationPatched
                    || e.Kind == HostEventKind.OperatingSystemPatched
                    || e.Kind == HostEventKind.PatchNowPickedUp))
            .Select(e => new PatchActivityRecord(e.HostId, e.Kind, e.Subject, e.OccurredUtc))
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<HostEvent>> GetEventsSinceAsync(DateTimeOffset sinceUtc, int take, CancellationToken cancellationToken) =>
        await _context.HostEvents
            .AsNoTracking()
            .Where(e => e.OccurredUtc >= sinceUtc)
            .OrderByDescending(e => e.OccurredUtc)
            .Take(take)
            .ToListAsync(cancellationToken);

    public Task<DateTimeOffset?> GetOldestEventUtcAsync(CancellationToken cancellationToken) =>
        _context.HostEvents
            .OrderBy(e => e.OccurredUtc)
            .Select(e => (DateTimeOffset?)e.OccurredUtc)
            .FirstOrDefaultAsync(cancellationToken);

    public Task<int> DeleteOlderThanAsync(DateTimeOffset cutoffUtc, CancellationToken cancellationToken) =>
        _context.HostEvents.Where(e => e.OccurredUtc < cutoffUtc).ExecuteDeleteAsync(cancellationToken);
}
