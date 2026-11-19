using Kintsugi.Domain.Entities;

namespace Kintsugi.Application.Common.Interfaces;

public interface IHostRepository
{
    Task<Host?> GetByIdAsync(Guid id, CancellationToken cancellationToken);
    Task<Host?> GetBySerialNumberAsync(string serialNumber, CancellationToken cancellationToken);

    /// <summary>The host holding this hostname, removed or not. Hostname is uniquely indexed, so
    /// this is what stands between a re-registering host and an unhandled constraint violation —
    /// see CreateHostCommandHandler. Deliberately not filtered on <see cref="Host.DeletedAtUtc"/>:
    /// a soft-deleted row still owns its name as far as the index is concerned, and a lookup that
    /// hid it would report the name as free right up until the insert failed.</summary>
    Task<Host?> GetByHostnameAsync(string hostname, CancellationToken cancellationToken);

    /// <summary>Every host that hasn't been removed — see <see cref="Host.DeletedAtUtc"/>. A host
    /// pending removal disappears from this list the moment removal is requested, well before the
    /// agent has actually confirmed it uninstalled itself.</summary>
    Task<IReadOnlyList<Host>> GetAllAsync(CancellationToken cancellationToken);

    /// <summary>The hosts with these ids that have not been removed, **tracked** so their inventory details can be
    /// changed and saved. Ids that match nothing are simply absent.</summary>
    Task<IReadOnlyList<Host>> GetByIdsAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken);

    /// <summary>The hosts holding any of these serial numbers (compared ignoring case) that have not been removed,
    /// tracked, for the inventory import.</summary>
    Task<IReadOnlyList<Host>> GetBySerialNumbersAsync(IReadOnlyCollection<string> serialNumbers, CancellationToken cancellationToken);

    Task AddAsync(Host host, CancellationToken cancellationToken);

    /// <summary>Permanently removes the host record — only ever called once the agent has
    /// confirmed it finished uninstalling itself from the host machine (see
    /// ConfirmHostRemovalCommandHandler), at which point there's nothing left to keep even
    /// soft-deleted for.</summary>
    Task DeleteAsync(Host host, CancellationToken cancellationToken);
}
