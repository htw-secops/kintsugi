using Kintsugi.Domain.Entities;

namespace Kintsugi.Application.Common.Interfaces;

public interface ISoftwareTitleRepository
{
    Task<IReadOnlyList<SoftwareTitle>> GetAllAsync(CancellationToken cancellationToken);

    Task<SoftwareTitle?> GetAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>The title stored under this (name, platform bucket), ignoring the name's case. With an OS
    /// bucket as <paramref name="platform"/> it can only find a custom title, since a package-manager
    /// title's bucket is its manager's.</summary>
    Task<SoftwareTitle?> FindAsync(string name, string platform, CancellationToken cancellationToken);

    /// <summary>Whether any title names this secret — what stops a secret in use being deleted.</summary>
    Task<bool> AnyUsesSecretAsync(string secretName, CancellationToken cancellationToken);

    Task AddAsync(SoftwareTitle title, CancellationToken cancellationToken);

    void Remove(SoftwareTitle title);
}
