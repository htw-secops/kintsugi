using Microsoft.EntityFrameworkCore;
using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Domain.Entities;

namespace Kintsugi.Infrastructure.Persistence.Repositories;

public class AppearanceSettingsRepository : IAppearanceSettingsRepository
{
    private readonly ApplicationDbContext _context;

    public AppearanceSettingsRepository(ApplicationDbContext context)
    {
        _context = context;
    }

    public Task<AppearanceSettings?> GetAsync(CancellationToken cancellationToken) =>
        _context.AppearanceSettings.FirstOrDefaultAsync(cancellationToken);

    public async Task AddAsync(AppearanceSettings settings, CancellationToken cancellationToken) =>
        await _context.AppearanceSettings.AddAsync(settings, cancellationToken);
}
