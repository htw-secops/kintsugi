using MediatR;

namespace Kintsugi.Application.PatchFailures.Commands.ReverifyPatchFailures;

/// <summary>
/// "Re-verify now": re-compares outstanding failures against the last inventory each host sent.
/// Both filters null means every outstanding failure.
/// </summary>
/// <remarks>It does not ask the device to rescan; a fresh read arrives with the agent's next hourly
/// check-in, and the next reconcile after that picks it up.</remarks>
public record ReverifyPatchFailuresCommand(Guid? HostId, string? ApplicationName) : IRequest<ReconcileResult>;

public class ReverifyPatchFailuresCommandHandler : IRequestHandler<ReverifyPatchFailuresCommand, ReconcileResult>
{
    private readonly PatchFailureReconciler _reconciler;
    private readonly Common.Interfaces.IUnitOfWork _unitOfWork;

    public ReverifyPatchFailuresCommandHandler(PatchFailureReconciler reconciler, Common.Interfaces.IUnitOfWork unitOfWork)
    {
        _reconciler = reconciler;
        _unitOfWork = unitOfWork;
    }

    public async Task<ReconcileResult> Handle(ReverifyPatchFailuresCommand request, CancellationToken cancellationToken)
    {
        var result = await _reconciler.ReconcileAsync(request.HostId, request.ApplicationName, DateTimeOffset.UtcNow, cancellationToken);
        await _unitOfWork.SaveChangesAsync(cancellationToken);
        return result;
    }
}

/// <summary>Deletes failures settled by a success report or the inventory more than <see cref="RetentionDays"/>
/// days ago. Run by the daily job. Dismissed and repaired rows are kept: see
/// <c>IPatchFailureRepository.DeleteResolvedBeforeAsync</c>.</summary>
public record PrunePatchFailuresCommand : IRequest<int>
{
    /// <summary>90, not 30: a failure that comes back monthly must still find its row, with its count.</summary>
    public const int RetentionDays = 90;
}

public class PrunePatchFailuresCommandHandler : IRequestHandler<PrunePatchFailuresCommand, int>
{
    private readonly Common.Interfaces.IPatchFailureRepository _failures;

    public PrunePatchFailuresCommandHandler(Common.Interfaces.IPatchFailureRepository failures) => _failures = failures;

    public Task<int> Handle(PrunePatchFailuresCommand request, CancellationToken cancellationToken) =>
        _failures.DeleteResolvedBeforeAsync(DateTimeOffset.UtcNow.AddDays(-PrunePatchFailuresCommand.RetentionDays), cancellationToken);
}
