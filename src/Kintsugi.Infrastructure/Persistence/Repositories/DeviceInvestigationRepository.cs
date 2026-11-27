using Microsoft.EntityFrameworkCore;
using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Domain.Entities;

namespace Kintsugi.Infrastructure.Persistence.Repositories;

public class DeviceInvestigationRepository : IDeviceInvestigationRepository
{
    private readonly ApplicationDbContext _context;

    public DeviceInvestigationRepository(ApplicationDbContext context)
    {
        _context = context;
    }

    public Task<DeviceInvestigationSettings?> GetSettingsAsync(CancellationToken cancellationToken) =>
        _context.DeviceInvestigationSettings.FirstOrDefaultAsync(cancellationToken);

    public async Task AddSettingsAsync(DeviceInvestigationSettings settings, CancellationToken cancellationToken) =>
        await _context.DeviceInvestigationSettings.AddAsync(settings, cancellationToken);

    public async Task AddAsync(DeviceInvestigation investigation, CancellationToken cancellationToken) =>
        await _context.DeviceInvestigations.AddAsync(investigation, cancellationToken);

    public Task<DeviceInvestigation?> GetLatestForHostAsync(Guid hostId, CancellationToken cancellationToken) =>
        _context.DeviceInvestigations
            .Where(i => i.HostId == hostId)
            .OrderByDescending(i => i.OpenedUtc)
            .FirstOrDefaultAsync(cancellationToken);

    public async Task<IReadOnlyList<DeviceInvestigation>> GetUnfinishedAsync(DateTimeOffset alertSinceUtc, CancellationToken cancellationToken) =>
        await _context.DeviceInvestigations
            .Where(i => i.ReportedUtc == null
                || (i.OpenedUtc >= alertSinceUtc && i.AlertedUtc == null && i.AlertAttempts < DeviceInvestigation.MaxAlertAttempts))
            .OrderBy(i => i.OpenedUtc)
            .ToListAsync(cancellationToken);
}
