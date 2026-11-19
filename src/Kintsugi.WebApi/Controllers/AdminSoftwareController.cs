using MediatR;
using Microsoft.AspNetCore.Mvc;
using Kintsugi.Application.Authentication;
using Kintsugi.Application.ForcedPatchRuns;
using Kintsugi.Application.SoftwareTitles;
using Kintsugi.Application.SoftwareTitles.Commands.AssignSoftwareTitle;
using Kintsugi.Application.SoftwareTitles.Commands.CreateSoftwareTitle;
using Kintsugi.Application.SoftwareTitles.Commands.DeleteScriptSecret;
using Kintsugi.Application.SoftwareTitles.Commands.DeleteSoftwareTitle;
using Kintsugi.Application.SoftwareTitles.Commands.RunSoftwareTitleNow;
using Kintsugi.Application.SoftwareTitles.Commands.SetScriptSecret;
using Kintsugi.Application.SoftwareTitles.Commands.UpdateSoftwareTitle;
using Kintsugi.Application.SoftwareTitles.Queries.GetSoftware;
using Kintsugi.Domain.Enums;
using Kintsugi.WebApi.Filters;

namespace Kintsugi.WebApi.Controllers;

/// <summary>
/// The Software screen: software titles to install, offer or remove, who they apply to, and the secrets
/// their scripts read. Every call but Run now answers with the whole screen, <see cref="SoftwareScreenDto"/>.
/// The agent's end is its ordinary work list, <c>GET /api/upgrade-paths</c>, which carries the action rows.
/// </summary>
/// <remarks>
/// <c>[RequireAdminSession]</c> on the class and under <c>/api/admin/</c>, like every browser-driven
/// controller, so nginx's general <c>/api</c> location covers it and no agent route was added. Saving a
/// title or a secret is what signs it, which is why only a signed-in administrator can. Who created,
/// assigned, ran or set anything is taken from the session, never from the body. Secret values come in
/// and never go out.
/// </remarks>
[ApiController]
[Route("api/admin/software")]
[Produces("application/json")]
[RequireAdminSession]
public class AdminSoftwareController : ControllerBase
{
    private readonly ISender _sender;

    public AdminSoftwareController(ISender sender)
    {
        _sender = sender;
    }

    [HttpGet]
    [ProducesResponseType(typeof(SoftwareScreenDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<SoftwareScreenDto>> Get(CancellationToken cancellationToken) =>
        Ok(await _sender.Send(new GetSoftwareQuery(), cancellationToken));

    [HttpPost]
    [ProducesResponseType(typeof(SoftwareScreenDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<SoftwareScreenDto>> Create(CreateSoftwareTitleRequest request, CancellationToken cancellationToken) =>
        Ok(await _sender.Send(
            new CreateSoftwareTitleCommand(
                request.Name ?? string.Empty, request.PackageManager, request.Platform, request.PackageId, request.DetectAs,
                request.Mode, request.SecretNames ?? Array.Empty<string>(), AdminIdentity.SignerOf(User)),
            cancellationToken));

    [HttpPut("{id:guid}")]
    [ProducesResponseType(typeof(SoftwareScreenDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<SoftwareScreenDto>> Update(Guid id, UpdateSoftwareTitleRequest request, CancellationToken cancellationToken) =>
        Ok(await _sender.Send(
            new UpdateSoftwareTitleCommand(id, request.Mode, request.DetectAs, request.SecretNames ?? Array.Empty<string>()),
            cancellationToken));

    [HttpPut("{id:guid}/assignment")]
    [ProducesResponseType(typeof(SoftwareScreenDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<SoftwareScreenDto>> Assign(Guid id, AssignSoftwareTitleRequest request, CancellationToken cancellationToken) =>
        Ok(await _sender.Send(
            new AssignSoftwareTitleCommand(id, request.AllHosts, request.HostNames ?? Array.Empty<string>(), AdminIdentity.SignerOf(User)),
            cancellationToken));

    /// <summary>Asks every host still pending (or failed) to act now, through forced patch runs.</summary>
    [HttpPost("{id:guid}/run-now")]
    [ProducesResponseType(typeof(RequestForcedPatchRunsResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<RequestForcedPatchRunsResult>> RunNow(Guid id, CancellationToken cancellationToken) =>
        Ok(await _sender.Send(new RunSoftwareTitleNowCommand(id, AdminIdentity.SignerOf(User)), cancellationToken));

    /// <summary>Stops Kintsugi acting on the title. Removes nothing already installed.</summary>
    [HttpDelete("{id:guid}")]
    [ProducesResponseType(typeof(SoftwareScreenDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<SoftwareScreenDto>> Delete(Guid id, CancellationToken cancellationToken) =>
        Ok(await _sender.Send(new DeleteSoftwareTitleCommand(id), cancellationToken));

    /// <summary>Sets a secret's value, creating it if it is new. Write-only: no call returns the value.</summary>
    [HttpPut("secrets/{name}")]
    [ProducesResponseType(typeof(SoftwareScreenDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<SoftwareScreenDto>> SetSecret(string name, SetScriptSecretRequest request, CancellationToken cancellationToken) =>
        Ok(await _sender.Send(new SetScriptSecretCommand(name, request.Value ?? string.Empty, AdminIdentity.SignerOf(User)), cancellationToken));

    /// <summary>Deletes a secret; 409 while any title uses it.</summary>
    [HttpDelete("secrets/{name}")]
    [ProducesResponseType(typeof(SoftwareScreenDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<SoftwareScreenDto>> DeleteSecret(string name, CancellationToken cancellationToken) =>
        Ok(await _sender.Send(new DeleteScriptSecretCommand(name), cancellationToken));
}

/// <summary>The body of <c>POST /api/admin/software</c>. <see cref="PackageManager"/> null makes a custom
/// title, for which <see cref="Platform"/> is "Windows", "macOS" or "Linux".</summary>
public record CreateSoftwareTitleRequest(
    string? Name,
    string? PackageManager,
    string? Platform,
    string? PackageId,
    string? DetectAs,
    SoftwareMode Mode,
    IReadOnlyList<string>? SecretNames);

/// <summary>The body of <c>PUT /api/admin/software/{id}</c>. Name, package id and source cannot change.</summary>
public record UpdateSoftwareTitleRequest(SoftwareMode Mode, string? DetectAs, IReadOnlyList<string>? SecretNames);

/// <summary>The body of <c>PUT /api/admin/software/{id}/assignment</c>.</summary>
public record AssignSoftwareTitleRequest(bool AllHosts, IReadOnlyList<string>? HostNames);

/// <summary>The body of <c>PUT /api/admin/software/secrets/{name}</c>.</summary>
public record SetScriptSecretRequest(string? Value)
{
    public override string ToString() => $"{nameof(SetScriptSecretRequest)} {{ Value = [redacted] }}";
}
