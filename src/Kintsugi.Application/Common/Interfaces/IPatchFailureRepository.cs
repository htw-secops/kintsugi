using Kintsugi.Application.PatchFailures;
using Kintsugi.Domain.Entities;

namespace Kintsugi.Application.Common.Interfaces;

public interface IPatchFailureRepository
{
    Task<PatchFailure?> GetByIdAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>The failure row already open for this (host, application), if there is one — what
    /// <c>ReportPatchFailureCommandHandler</c> folds a repeat into rather than opening a second row.
    /// Deliberately not filtered on <see cref="Domain.Entities.PatchFailure.Resolution"/>: a
    /// dismissed or since-succeeded row that starts failing again is the same problem returning,
    /// and is reopened rather than duplicated (see <c>PatchFailure.Reopen</c>).</summary>
    Task<PatchFailure?> GetByHostAndApplicationAsync(Guid hostId, string applicationName, CancellationToken cancellationToken);

    /// <summary>Every failure still outstanding for one host's application — what a success report
    /// closes. A list rather than one row because nothing stops two rows existing for names that
    /// differ only in case, which is how the agents match applications everywhere else.</summary>
    Task<IReadOnlyList<PatchFailure>> GetOutstandingForApplicationAsync(Guid hostId, string applicationName, CancellationToken cancellationToken);

    /// <summary>Every outstanding failure against one (application, platform) upgrade path, across
    /// every host — what signing a repaired script clears.</summary>
    /// <remarks>
    /// Fleet-wide rather than per host, because a script is stored per (application, platform) and
    /// not per host: the signature that replaces a broken script replaces it for every machine that
    /// was failing on it. Clearing only the row the operator happened to be looking at would leave
    /// the queue asserting that the other hosts are still broken by a script that no longer exists.
    /// </remarks>
    Task<IReadOnlyList<PatchFailure>> GetOutstandingForPathAsync(string applicationName, string platform, CancellationToken cancellationToken);

    /// <summary>Every outstanding failure on one host — what the inventory reconciliation checks
    /// each time that host reports its applications.</summary>
    Task<IReadOnlyList<PatchFailure>> GetOutstandingForHostAsync(Guid hostId, CancellationToken cancellationToken);

    /// <summary>Every outstanding failure, fleet-wide, oldest first — what the daily reconciliation
    /// walks and the AI classification pass takes its batches from.</summary>
    Task<IReadOnlyList<PatchFailure>> GetAllOutstandingAsync(CancellationToken cancellationToken);

    /// <summary>Deletes failures settled by a success report or by the inventory (not the ones an
    /// administrator dismissed or repaired) before the cutoff, returning how many. Those two are history
    /// nobody acts on; the human-settled ones are kept, since a problem that returns reopens them with its
    /// count and first-failed date intact.</summary>
    Task<int> DeleteResolvedBeforeAsync(DateTimeOffset cutoffUtc, CancellationToken cancellationToken);

    Task AddAsync(PatchFailure failure, CancellationToken cancellationToken);

    /// <summary>Every failure, joined with the host that reported it and the upgrade path it is
    /// about — what the Failed Updates screen reads. Fleet-wide and unpaged, like the Applications
    /// screen's own listing: this is bounded by how many things are actually broken, not by fleet
    /// size, because repeats fold into one row per (host, application).</summary>
    Task<IReadOnlyList<PatchFailureDto>> GetAllAsync(CancellationToken cancellationToken);

    /// <summary>How many failures are still <c>Outstanding</c> on each host, counted in the database — what the Hosts
    /// list's Health column needs, without loading every row (and every upgrade path) as <see cref="GetAllAsync"/>
    /// does. A host with none is absent.</summary>
    Task<IReadOnlyDictionary<Guid, int>> CountOutstandingByHostAsync(CancellationToken cancellationToken);
}
