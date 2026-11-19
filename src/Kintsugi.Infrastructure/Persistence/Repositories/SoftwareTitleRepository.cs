using Microsoft.EntityFrameworkCore;
using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Domain.Entities;

namespace Kintsugi.Infrastructure.Persistence.Repositories;

public class SoftwareTitleRepository : ISoftwareTitleRepository
{
    private readonly ApplicationDbContext _context;

    public SoftwareTitleRepository(ApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<IReadOnlyList<SoftwareTitle>> GetAllAsync(CancellationToken cancellationToken) =>
        await _context.SoftwareTitles.OrderBy(t => t.Name).ToListAsync(cancellationToken);

    public Task<SoftwareTitle?> GetAsync(Guid id, CancellationToken cancellationToken) =>
        _context.SoftwareTitles.FirstOrDefaultAsync(t => t.Id == id, cancellationToken);

    // Case-insensitive, like UpgradePathRepository.GetAsync: a custom title's script is found by the
    // same (name, platform) lookup, and the unique index alone would let "Zoom" and "zoom" coexist.
    public Task<SoftwareTitle?> FindAsync(string name, string platform, CancellationToken cancellationToken) =>
        _context.SoftwareTitles.FirstOrDefaultAsync(
            t => t.Name.ToLower() == name.ToLower() && t.Platform == platform, cancellationToken);

    public Task<bool> AnyUsesSecretAsync(string secretName, CancellationToken cancellationToken) =>
        _context.SoftwareTitles.AnyAsync(t => t.SecretNames.Contains(secretName), cancellationToken);

    public async Task AddAsync(SoftwareTitle title, CancellationToken cancellationToken) =>
        await _context.SoftwareTitles.AddAsync(title, cancellationToken);

    public void Remove(SoftwareTitle title) => _context.SoftwareTitles.Remove(title);
}
