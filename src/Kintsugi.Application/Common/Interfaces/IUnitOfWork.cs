namespace Kintsugi.Application.Common.Interfaces;

public interface IUnitOfWork
{
    Task<int> SaveChangesAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Forgets every change not yet saved, so a caller whose save failed can record that failure in a fresh
    /// save instead of retrying the one that broke. Tracked entities are detached: read them again after.
    /// </summary>
    void DiscardChanges();
}
