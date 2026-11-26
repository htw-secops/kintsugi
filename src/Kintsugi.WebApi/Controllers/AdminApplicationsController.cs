using MediatR;
using Microsoft.AspNetCore.Mvc;
using Kintsugi.Application.Applications.Queries.GetApplicationOverview;
using Kintsugi.Application.ForcedPatchRuns;
using Kintsugi.Application.ForcedPatchRuns.Commands.RequestForcedPatchRuns;
using Kintsugi.Application.UpgradePaths.Queries.GetUpgradePathScript;
using Kintsugi.WebApi.Filters;

namespace Kintsugi.WebApi.Controllers;

/// <summary>
/// The Applications screen's own data, joined server-side.
/// </summary>
/// <remarks>
/// <para>
/// Under <c>/api/admin</c> rather than on <c>ApplicationsController</c> because that controller is
/// routed at <c>/api/applications</c>, which is one of the paths inside nginx's exact-match agent
/// regex — a route there requires a fleet client certificate that a browser does not have. Every
/// browser-driven route added from here on lives under this prefix for exactly that reason: it
/// cannot collide with that regex however the regex grows, and the collision is otherwise silent
/// (the call simply 403s with nothing in the C# to explain why).
/// </para>
/// <para>
/// The attribute is on the class, matching the precedent set by <see cref="AiSettingsController"/>
/// and the others: nothing here is an agent route, and the recurring failure is a route added
/// later inheriting no gate at all. Excluding a route from nginx's regex does not make it
/// browser-only, it makes it anonymous.
/// </para>
/// </remarks>
[ApiController]
[Route("api/admin/applications")]
[Produces("application/json")]
[RequireAdminSession]
public class AdminApplicationsController : ControllerBase
{
    private readonly ISender _sender;

    public AdminApplicationsController(ISender sender)
    {
        _sender = sender;
    }

    [HttpGet]
    [ProducesResponseType(typeof(ApplicationOverviewDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApplicationOverviewDto>> Get(CancellationToken cancellationToken) =>
        Ok(await _sender.Send(new GetApplicationOverviewQuery(), cancellationToken));

    /// <summary>
    /// One upgrade path's script, for the Applications screen's "View script". The overview above
    /// says only whether each row has one: it is polled every minute per open tab, and carrying
    /// the scripts made it 14 MB a poll on production (2026-11-27).
    /// </summary>
    /// <param name="applicationName">The row's application name, matched without regard to case.</param>
    /// <param name="platform">The row's platform bucket, exactly as the overview reported it.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    [HttpGet("script")]
    [ProducesResponseType(typeof(UpgradePathScriptDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<UpgradePathScriptDto>> GetScript(
        [FromQuery] string applicationName,
        [FromQuery] string platform,
        CancellationToken cancellationToken) =>
        await _sender.Send(new GetUpgradePathScriptQuery(applicationName, platform), cancellationToken) is { } script
            ? Ok(script)
            : NotFound();

    /// <summary>
    /// Tells the named hosts to run one application's upgrade script at their next opportunity,
    /// rather than waiting for their own patching schedule — the Applications screen's "Patch now"
    /// action, for an emergency an administrator is not willing to wait a patching interval for.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The agent's end of the same feature is <c>GET /api/forced-patch-runs</c> on
    /// <see cref="ForcedPatchRunsController"/>, which is inside nginx's agent regex and therefore
    /// demands a fleet client certificate. This one is browser-driven, so it lives here, under the
    /// prefix that regex can never match, behind the class's <c>[RequireAdminSession]</c>.
    /// </para>
    /// <para>
    /// It raises an instruction and nothing more: no script crosses this route, and forcing a run
    /// cannot make an unsigned script runnable. See the remarks on <c>ForcedPatchRun</c>.
    /// </para>
    /// </remarks>
    [HttpPost("forced-patch-runs")]
    [ProducesResponseType(typeof(RequestForcedPatchRunsResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<RequestForcedPatchRunsResult>> ForcePatchRuns(
        RequestForcedPatchRunsCommand command,
        CancellationToken cancellationToken) =>
        // RequestedBy is the signed-in administrator, set here and not read from the body, so the
        // name on the host's timeline cannot be chosen by the caller.
        Ok(await _sender.Send(command with { RequestedBy = AdminRemoteControlController.DescribeRequester(User) }, cancellationToken));
}
