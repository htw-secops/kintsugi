using Kintsugi.Domain.Entities;

namespace Kintsugi.Application.Common.Interfaces;

public interface IKevBacklogSnapshotRepository
{
    Task<bool> ExistsForDateAsync(DateTimeOffset dateUtc, CancellationToken cancellationToken);

    Task<IReadOnlyList<KevBacklogSnapshot>> GetSinceAsync(DateTimeOffset sinceUtc, CancellationToken cancellationToken);

    Task AddAsync(KevBacklogSnapshot snapshot, CancellationToken cancellationToken);
}
