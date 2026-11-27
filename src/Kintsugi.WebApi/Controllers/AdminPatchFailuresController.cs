using MediatR;
using Microsoft.AspNetCore.Mvc;
using Kintsugi.Application.PatchFailures;
using Kintsugi.Application.PatchFailures.Commands.DismissPatchFailure;
using Kintsugi.Application.PatchFailures.Commands.ReverifyPatchFailures;
using Kintsugi.Application.PatchFailures.Queries.GetPatchFailureRepairDraft;
using Kintsugi.Application.PatchFailures.Queries.GetPatchFailures;
using Kintsugi.WebApi.Filters;

namespace Kintsugi.WebApi.Controllers;

/// <summary>
/// The Failed Updates screen's data: every upgrade an agent tried to apply and could not, with the
/// host, the date and the failing script's own output.
/// </summary>
/// <remarks>
/// <para>
/// Under <c>/api/admin</c> for the reason <see cref="AdminApplicationsController"/> gives at length —
/// a browser-driven route must not sit on a path inside nginx's exact-match agent regex. The
/// agent-facing half of this feature is <c>POST /api/patch-failures</c> on
/// <see cref="ApplicationsController"/>, which *is* in that regex and carries
/// <see cref="RequireAgentIdentityAttribute"/>.
/// </para>
/// <para>
/// The attribute is on the class, matching the precedent the other admin controllers set: nothing
/// here is an agent route, and the recurring failure is a route added later inheriting no gate.
/// </para>
/// </remarks>
[ApiController]
[Route("api/admin/patch-failures")]
[Produces("application/json")]
[RequireAdminSession]
public class AdminPatchFailuresController : ControllerBase
{
    private readonly ISender _sender;

    public AdminPatchFailuresController(ISender sender)
    {
        _sender = sender;
    }

    /// <summary>Every reported patch failure, outstanding ones first.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<PatchFailureDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<PatchFailureDto>>> GetAll(CancellationToken cancellationToken) =>
        Ok(await _sender.Send(new GetPatchFailuresQuery(), cancellationToken));

    /// <summary>
    /// The repair drafted in the background for the script this failure ran, beside that script, for
    /// review. 404 when there is none. Signing it is <c>POST /api/upgrade-paths/sign-script</c> with
    /// <c>signCandidate</c>; nothing here signs.
    /// </summary>
    [HttpGet("{id:guid}/repair-draft")]
    [ProducesResponseType(typeof(PatchFailureRepairDraftDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PatchFailureRepairDraftDto>> GetRepairDraft(Guid id, CancellationToken cancellationToken) =>
        Ok(await _sender.Send(new GetPatchFailureRepairDraftQuery(id), cancellationToken));

    /// <summary>
    /// Clears one failure off the screen by hand. For failures that cannot recur — everything else
    /// closes itself when the host next reports that application patched successfully.
    /// </summary>
    [HttpPost("{id:guid}/dismiss")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Dismiss(Guid id, CancellationToken cancellationToken)
    {
        await _sender.Send(new DismissPatchFailureCommand(id, AdminRemoteControlController.DescribeRequester(User)), cancellationToken);
        return NoContent();
    }

    /// <summary>
    /// Re-checks outstanding failures against the last inventory each host sent and closes the ones
    /// that are provably false alarms. Both fields null means every outstanding failure. It does
    /// not rescan the device — a fresh read arrives with the agent's next hourly check-in.
    /// </summary>
    [HttpPost("reverify")]
    [ProducesResponseType(typeof(ReconcileResult), StatusCodes.Status200OK)]
    public async Task<ActionResult<ReconcileResult>> Reverify([FromBody] ReverifyRequest request, CancellationToken cancellationToken) =>
        Ok(await _sender.Send(new ReverifyPatchFailuresCommand(request.HostId, request.ApplicationName), cancellationToken));

    public record ReverifyRequest(Guid? HostId, string? ApplicationName);
}
