using Microsoft.EntityFrameworkCore;
using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Domain.Entities;

namespace Kintsugi.Infrastructure.Persistence.Repositories;

public class OutlookSignatureSourceSettingsRepository : IOutlookSignatureSourceSettingsRepository
{
    private readonly ApplicationDbContext _context;

    public OutlookSignatureSourceSettingsRepository(ApplicationDbContext context)
    {
        _context = context;
    }

    public Task<OutlookSignatureSourceSettings?> GetAsync(CancellationToken cancellationToken) =>
        _context.OutlookSignatureSourceSettings.FirstOrDefaultAsync(cancellationToken);

    public async Task AddAsync(OutlookSignatureSourceSettings settings, CancellationToken cancellationToken) =>
        await _context.OutlookSignatureSourceSettings.AddAsync(settings, cancellationToken);
}
