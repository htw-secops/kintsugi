using Microsoft.EntityFrameworkCore;
using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Domain.Entities;

namespace Kintsugi.Infrastructure.Persistence.Repositories;

public class WindowsPatchBaselineRepository : IWindowsPatchBaselineRepository
{
    private readonly ApplicationDbContext _context;

    public WindowsPatchBaselineRepository(ApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<IReadOnlyList<WindowsPatchBaseline>> GetAllAsync(CancellationToken cancellationToken) =>
        await _context.WindowsPatchBaselines.AsNoTracking().ToListAsync(cancellationToken);
}
