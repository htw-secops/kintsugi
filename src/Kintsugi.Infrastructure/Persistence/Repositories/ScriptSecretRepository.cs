using Microsoft.EntityFrameworkCore;
using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Domain.Entities;

namespace Kintsugi.Infrastructure.Persistence.Repositories;

public class ScriptSecretRepository : IScriptSecretRepository
{
    private readonly ApplicationDbContext _context;

    public ScriptSecretRepository(ApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<IReadOnlyList<ScriptSecret>> GetAllAsync(CancellationToken cancellationToken) =>
        await _context.ScriptSecrets.OrderBy(s => s.Name).ToListAsync(cancellationToken);

    // Exact match: names are upper-case by rule, and the environment variable is spelt exactly so.
    public Task<ScriptSecret?> GetAsync(string name, CancellationToken cancellationToken) =>
        _context.ScriptSecrets.FirstOrDefaultAsync(s => s.Name == name, cancellationToken);

    public async Task AddAsync(ScriptSecret secret, CancellationToken cancellationToken) =>
        await _context.ScriptSecrets.AddAsync(secret, cancellationToken);

    public void Remove(ScriptSecret secret) => _context.ScriptSecrets.Remove(secret);
}
