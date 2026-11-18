using Microsoft.EntityFrameworkCore;
using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Domain.Entities;

namespace Kintsugi.Infrastructure.Persistence.Repositories;

public class PersonSignatureRepository : IPersonSignatureRepository
{
    private readonly ApplicationDbContext _context;

    public PersonSignatureRepository(ApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<IReadOnlyList<PersonSignature>> GetAllAsync(CancellationToken cancellationToken) =>
        await _context.PersonSignatures.ToListAsync(cancellationToken);

    public Task<int> CountAsync(CancellationToken cancellationToken) =>
        _context.PersonSignatures.CountAsync(cancellationToken);

    public async Task AddAsync(PersonSignature signature, CancellationToken cancellationToken) =>
        await _context.PersonSignatures.AddAsync(signature, cancellationToken);

    public void Remove(IEnumerable<PersonSignature> signatures) =>
        _context.PersonSignatures.RemoveRange(signatures);
}
