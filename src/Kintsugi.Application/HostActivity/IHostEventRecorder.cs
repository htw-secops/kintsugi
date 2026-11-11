namespace Kintsugi.Application.HostActivity;

/// <summary>
/// Writes to a host's activity timeline. Each method adds to the unit of work and leaves the
/// save to the calling handler, so an event is stored in the same transaction as the change it
/// describes — never one without the other.
/// </summary>
public interface IHostEventRecorder
{
    Task PatchNowRequestedAsync(Guid hostId, string subject, string actor, DateTimeOffset at, CancellationToken cancellationToken);

    Task PatchNowPickedUpAsync(Guid hostId, string subject, DateTimeOffset at, CancellationToken cancellationToken);

    Task ApplicationPatchedAsync(Guid hostId, string subject, string? fromVersion, string toVersion, DateTimeOffset at, CancellationToken cancellationToken);

    Task OperatingSystemPatchedAsync(Guid hostId, string subject, DateTimeOffset at, CancellationToken cancellationToken);

    /// <summary>Folds into the latest event when that is the same failure still going on.</summary>
    Task PatchFailedAsync(Guid hostId, string subject, string? installedVersion, string? attemptedVersion, string details, DateTimeOffset at, CancellationToken cancellationToken);

    Task FailureDismissedAsync(Guid hostId, string subject, string actor, DateTimeOffset at, CancellationToken cancellationToken);
}
