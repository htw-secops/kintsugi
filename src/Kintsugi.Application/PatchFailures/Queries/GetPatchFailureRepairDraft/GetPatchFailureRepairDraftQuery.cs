using Kintsugi.Application.Common.Exceptions;
using Kintsugi.Application.Common.Interfaces;
using MediatR;

namespace Kintsugi.Application.PatchFailures.Queries.GetPatchFailureRepairDraft;

/// <summary>
/// The repair Kintsugi drafted for the script a failure ran, beside the script it would replace — what
/// the Failed Updates screen diffs before somebody signs the draft. Read per row, when the panel opens,
/// rather than carried on every row of the list: a script per failure, repeated for every host failing
/// on the same path, is most of a megabyte nobody asked for.
/// </summary>
public record GetPatchFailureRepairDraftQuery(Guid PatchFailureId) : IRequest<PatchFailureRepairDraftDto>;

/// <param name="CurrentScript">The stored script, the one agents run now.</param>
/// <param name="CandidateScript">The draft. Signing sends its SHA-256, so the text shown is the text signed.</param>
/// <remarks>Hand-mirrored in <c>web/lib/data/models/patch_failure_mapper.dart</c>; see
/// <c>.claude/rules/hand-mirrored-dtos.md</c>.</remarks>
public record PatchFailureRepairDraftDto(
    string ApplicationName,
    string Platform,
    string? CurrentScript,
    bool CurrentScriptSigned,
    string CandidateScript,
    string? CandidateNotes,
    DateTimeOffset CandidateDraftedUtc,
    Guid? CandidateFailureId);

public class GetPatchFailureRepairDraftQueryHandler : IRequestHandler<GetPatchFailureRepairDraftQuery, PatchFailureRepairDraftDto>
{
    private readonly IPatchFailureRepository _failures;
    private readonly IUpgradePathRepository _paths;

    public GetPatchFailureRepairDraftQueryHandler(IPatchFailureRepository failures, IUpgradePathRepository paths)
    {
        _failures = failures;
        _paths = paths;
    }

    public async Task<PatchFailureRepairDraftDto> Handle(GetPatchFailureRepairDraftQuery request, CancellationToken cancellationToken)
    {
        var failure = await _failures.GetByIdAsync(request.PatchFailureId, cancellationToken)
            ?? throw new NotFoundException($"No patch failure '{request.PatchFailureId}'.");

        var path = failure.Platform is null ? null : await _paths.GetAsync(failure.ApplicationName, failure.Platform, cancellationToken);
        if (path?.CandidateScript is null || path.CandidateDraftedUtc is null)
        {
            throw new NotFoundException($"No drafted repair is waiting for '{failure.ApplicationName}'.");
        }

        return new PatchFailureRepairDraftDto(
            path.ApplicationName, path.Platform, path.Script, path.ScriptSignature is not null,
            path.CandidateScript, path.CandidateNotes, path.CandidateDraftedUtc.Value, path.CandidateFailureId);
    }
}
