using Microsoft.EntityFrameworkCore;
using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Domain.Entities;

namespace Kintsugi.Infrastructure.Persistence.Repositories;

public class HostRepository : IHostRepository
{
    private readonly ApplicationDbContext _context;

    public HostRepository(ApplicationDbContext context)
    {
        _context = context;
    }

    public Task<Host?> GetByIdAsync(Guid id, CancellationToken cancellationToken) =>
        _context.Hosts.FirstOrDefaultAsync(h => h.Id == id, cancellationToken);

    public Task<Host?> GetBySerialNumberAsync(string serialNumber, CancellationToken cancellationToken) =>
        _context.Hosts.FirstOrDefaultAsync(h => h.SerialNumber == serialNumber, cancellationToken);

    public Task<Host?> GetByHostnameAsync(string hostname, CancellationToken cancellationToken) =>
        _context.Hosts.FirstOrDefaultAsync(h => h.Hostname == hostname, cancellationToken);

    public async Task<IReadOnlyList<Host>> GetAllAsync(CancellationToken cancellationToken) =>
        await _context.Hosts.AsNoTracking().Where(h => h.DeletedAtUtc == null).ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<Host>> GetByIdsAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken) =>
        await _context.Hosts.Where(h => h.DeletedAtUtc == null && ids.Contains(h.Id)).ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<Host>> GetBySerialNumbersAsync(IReadOnlyCollection<string> serialNumbers, CancellationToken cancellationToken)
    {
        var lowered = serialNumbers.Select(s => s.ToLowerInvariant()).ToList();
        return await _context.Hosts.Where(h => h.DeletedAtUtc == null && lowered.Contains(h.SerialNumber.ToLower())).ToListAsync(cancellationToken);
    }

    public async Task AddAsync(Host host, CancellationToken cancellationToken) =>
        await _context.Hosts.AddAsync(host, cancellationToken);

    public Task DeleteAsync(Host host, CancellationToken cancellationToken)
    {
        _context.Hosts.Remove(host);
        return Task.CompletedTask;
    }
}
