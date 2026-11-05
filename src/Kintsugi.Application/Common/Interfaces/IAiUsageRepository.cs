using Kintsugi.Domain.Entities;

namespace Kintsugi.Application.Common.Interfaces;

/// <summary>The AI usage ledger and the budget settings beside it.</summary>
public interface IAiUsageRepository
{
    Task<AiBudgetSettings?> GetBudgetAsync(CancellationToken cancellationToken);

    Task AddBudgetAsync(AiBudgetSettings settings, CancellationToken cancellationToken);

    Task AddUsageAsync(AiUsageRecord record, CancellationToken cancellationToken);

    /// <summary>Everything recorded at or after <paramref name="sinceUtc"/>, oldest first.</summary>
    Task<IReadOnlyList<AiUsageRecord>> GetSinceAsync(DateTimeOffset sinceUtc, CancellationToken cancellationToken);

    /// <summary>The estimated dollars spent at or after <paramref name="sinceUtc"/>. Calls with no
    /// known price count as nothing, since there is nothing to add.</summary>
    Task<decimal> SumCostUsdSinceAsync(DateTimeOffset sinceUtc, CancellationToken cancellationToken);
}
