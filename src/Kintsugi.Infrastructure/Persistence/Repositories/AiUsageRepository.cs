using Microsoft.EntityFrameworkCore;
using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Domain.Entities;

namespace Kintsugi.Infrastructure.Persistence.Repositories;

public class AiUsageRepository : IAiUsageRepository
{
    private readonly ApplicationDbContext _context;

    public AiUsageRepository(ApplicationDbContext context)
    {
        _context = context;
    }

    public Task<AiBudgetSettings?> GetBudgetAsync(CancellationToken cancellationToken) =>
        _context.AiBudgetSettings.FirstOrDefaultAsync(cancellationToken);

    public async Task AddBudgetAsync(AiBudgetSettings settings, CancellationToken cancellationToken) =>
        await _context.AiBudgetSettings.AddAsync(settings, cancellationToken);

    public async Task AddUsageAsync(AiUsageRecord record, CancellationToken cancellationToken) =>
        await _context.AiUsage.AddAsync(record, cancellationToken);

    public async Task<IReadOnlyList<AiUsageRecord>> GetSinceAsync(DateTimeOffset sinceUtc, CancellationToken cancellationToken) =>
        await _context.AiUsage.AsNoTracking()
            .Where(r => r.OccurredUtc >= sinceUtc)
            .OrderBy(r => r.OccurredUtc)
            .ToListAsync(cancellationToken);

    public async Task<decimal> SumCostUsdSinceAsync(DateTimeOffset sinceUtc, CancellationToken cancellationToken) =>
        await _context.AiUsage.AsNoTracking()
            .Where(r => r.OccurredUtc >= sinceUtc && r.EstimatedCostUsd != null)
            .SumAsync(r => r.EstimatedCostUsd!.Value, cancellationToken);
}
