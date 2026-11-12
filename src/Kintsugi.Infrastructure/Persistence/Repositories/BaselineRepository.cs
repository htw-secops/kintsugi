using Microsoft.EntityFrameworkCore;
using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Domain.Entities;

namespace Kintsugi.Infrastructure.Persistence.Repositories;

public class BaselineRepository : IBaselineRepository
{
    private readonly ApplicationDbContext _context;

    public BaselineRepository(ApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<IReadOnlyList<Baseline>> GetAllAsync(CancellationToken cancellationToken) =>
        await _context.Baselines.ToListAsync(cancellationToken);

    public Task<Baseline?> GetAsync(Guid id, CancellationToken cancellationToken) =>
        _context.Baselines.FirstOrDefaultAsync(b => b.Id == id, cancellationToken);

    // Case-insensitive, so "Windows workstation" and "windows workstation" are one baseline, not two.
    public Task<Baseline?> GetByNameAsync(string name, CancellationToken cancellationToken) =>
        _context.Baselines.FirstOrDefaultAsync(b => b.Name.ToLower() == name.ToLower(), cancellationToken);

    public async Task AddAsync(Baseline baseline, CancellationToken cancellationToken) =>
        await _context.Baselines.AddAsync(baseline, cancellationToken);

    public async Task<IReadOnlyList<BaselineVersion>> GetVersionsAsync(IReadOnlyCollection<Guid> baselineIds, CancellationToken cancellationToken) =>
        await _context.BaselineVersions
            .Where(v => baselineIds.Contains(v.BaselineId))
            .OrderByDescending(v => v.Number)
            .ToListAsync(cancellationToken);

    public Task<BaselineVersion?> GetVersionAsync(Guid versionId, CancellationToken cancellationToken) =>
        _context.BaselineVersions.FirstOrDefaultAsync(v => v.Id == versionId, cancellationToken);

    public async Task AddVersionAsync(BaselineVersion version, CancellationToken cancellationToken) =>
        await _context.BaselineVersions.AddAsync(version, cancellationToken);

    public async Task<IReadOnlyList<HostBaselineItemState>> GetStatesAsync(IReadOnlyCollection<Guid> versionIds, CancellationToken cancellationToken) =>
        await _context.HostBaselineItemStates.AsNoTracking()
            .Where(s => versionIds.Contains(s.BaselineVersionId))
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<HostBaselineItemState>> GetHostStatesAsync(Guid hostId, IReadOnlyCollection<Guid> versionIds, CancellationToken cancellationToken) =>
        await _context.HostBaselineItemStates
            .Where(s => s.HostId == hostId && versionIds.Contains(s.BaselineVersionId))
            .ToListAsync(cancellationToken);

    public async Task AddStateAsync(HostBaselineItemState state, CancellationToken cancellationToken) =>
        await _context.HostBaselineItemStates.AddAsync(state, cancellationToken);

    public void RemoveStates(IEnumerable<HostBaselineItemState> states) =>
        _context.HostBaselineItemStates.RemoveRange(states);

    public async Task<IReadOnlyList<HostFact>> GetHostFactsAsync(Guid hostId, CancellationToken cancellationToken) =>
        await _context.HostFacts.Where(f => f.HostId == hostId).ToListAsync(cancellationToken);

    public async Task AddFactAsync(HostFact fact, CancellationToken cancellationToken) =>
        await _context.HostFacts.AddAsync(fact, cancellationToken);
}
