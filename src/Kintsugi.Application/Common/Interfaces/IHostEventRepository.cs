using Kintsugi.Domain.Entities;

namespace Kintsugi.Application.Common.Interfaces;

public interface IHostEventRepository
{
    Task AddAsync(HostEvent hostEvent, CancellationToken cancellationToken);

    /// <summary>The most recent event for one host and subject, or null. Used to fold a repeated
    /// failure into the event already on the timeline.</summary>
    Task<HostEvent?> GetLatestForSubjectAsync(Guid hostId, string subject, CancellationToken cancellationToken);

    /// <summary>A host's events, newest first.</summary>
    Task<IReadOnlyList<HostEvent>> GetForHostAsync(Guid hostId, int take, CancellationToken cancellationToken);

    /// <summary>Removes every event older than <paramref name="cutoffUtc"/>; returns how many.</summary>
    Task<int> DeleteOlderThanAsync(DateTimeOffset cutoffUtc, CancellationToken cancellationToken);
}
