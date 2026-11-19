using Kintsugi.Domain.Entities;

namespace Kintsugi.Application.Common.Interfaces;

/// <summary>The signature copies pulled from the signature source — see <see cref="PersonSignature"/>.</summary>
public interface IPersonSignatureRepository
{
    /// <summary>Every copy, tracked, for the pull to replace in place.</summary>
    Task<IReadOnlyList<PersonSignature>> GetAllAsync(CancellationToken cancellationToken);

    Task<int> CountAsync(CancellationToken cancellationToken);

    /// <summary>Every email that has a copy — what the username fallback compares against. Emails only.</summary>
    Task<IReadOnlyList<string>> GetEmailsAsync(CancellationToken cancellationToken);

    /// <summary>The copies for exactly these (lower-cased) emails, untracked.</summary>
    Task<IReadOnlyList<PersonSignature>> GetByEmailsAsync(IReadOnlyCollection<string> emails, CancellationToken cancellationToken);

    Task AddAsync(PersonSignature signature, CancellationToken cancellationToken);

    void Remove(IEnumerable<PersonSignature> signatures);
}
