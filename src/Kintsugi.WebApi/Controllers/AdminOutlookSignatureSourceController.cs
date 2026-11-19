using MediatR;
using Microsoft.AspNetCore.Mvc;
using Kintsugi.Application.OutlookSignatures;
using Kintsugi.Application.OutlookSignatures.Commands.UpdateOutlookSignatureSource;
using Kintsugi.Application.OutlookSignatures.Queries.GetOutlookSignatureSource;
using Kintsugi.WebApi.Filters;

namespace Kintsugi.WebApi.Controllers;

/// <summary>
/// Where managed Outlook signatures are pulled from, and how the last pull went.
/// </summary>
/// <remarks>
/// <c>[RequireAdminSession]</c> on the class: nothing here is an agent route, and the endpoint URL decides
/// where this server sends an ID token for its own identity. The path is under <c>/api/admin/</c>, so it
/// is served by nginx's general <c>/api</c> location and can never match the agent-certificate regex;
/// <c>OutlookSignatureSourceRouteTests</c> holds both halves.
/// </remarks>
[ApiController]
[Route("api/admin/outlook-signature-source")]
[Produces("application/json")]
[RequireAdminSession]
public class AdminOutlookSignatureSourceController : ControllerBase
{
    private readonly ISender _sender;

    public AdminOutlookSignatureSourceController(ISender sender)
    {
        _sender = sender;
    }

    [HttpGet]
    [ProducesResponseType(typeof(OutlookSignatureSourceDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<OutlookSignatureSourceDto>> Get(CancellationToken cancellationToken) =>
        Ok(await _sender.Send(new GetOutlookSignatureSourceQuery(), cancellationToken));

    [HttpPut]
    [ProducesResponseType(typeof(OutlookSignatureSourceDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<OutlookSignatureSourceDto>> Update(
        UpdateOutlookSignatureSourceCommand command, CancellationToken cancellationToken) =>
        Ok(await _sender.Send(command, cancellationToken));
}
