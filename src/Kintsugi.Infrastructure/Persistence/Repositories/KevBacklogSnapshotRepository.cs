using Microsoft.EntityFrameworkCore;
using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Domain.Entities;

namespace Kintsugi.Infrastructure.Persistence.Repositories;

public class KevBacklogSnapshotRepository : IKevBacklogSnapshotRepository
{
    private readonly ApplicationDbContext _context;

    public KevBacklogSnapshotRepository(ApplicationDbContext context)
    {
        _context = context;
    }

    public Task<bool> ExistsForDateAsync(DateTimeOffset dateUtc, CancellationToken cancellationToken)
    {
        var day = new DateTimeOffset(dateUtc.UtcDateTime.Date, TimeSpan.Zero);
        return _context.KevBacklogSnapshots.AnyAsync(s => s.SnapshotDateUtc == day, cancellationToken);
    }

    public async Task<IReadOnlyList<KevBacklogSnapshot>> GetSinceAsync(DateTimeOffset sinceUtc, CancellationToken cancellationToken) =>
        await _context.KevBacklogSnapshots
            .AsNoTracking()
            .Where(s => s.SnapshotDateUtc >= sinceUtc)
            .OrderBy(s => s.SnapshotDateUtc)
            .ToListAsync(cancellationToken);

    public async Task AddAsync(KevBacklogSnapshot snapshot, CancellationToken cancellationToken) =>
        await _context.KevBacklogSnapshots.AddAsync(snapshot, cancellationToken);
}
