using Microsoft.AspNetCore.Mvc;
using Kintsugi.Application.Common.Interfaces;

namespace Kintsugi.WebApi.Controllers;

/// <summary>
/// Where an MSI that winget no longer lists can still be downloaded, asked for by the winget script
/// (<c>WingetUpgradeScript</c>'s <c>Save-RetiredMsiFromKintsugi</c>) when an installed version's cached MSI is gone
/// and <c>winget download</c> of that version fails. See <c>RetiredWingetInstallerLookup</c>.
/// </summary>
/// <remarks>
/// Anonymous, like the App Installer copy beside it and for the same reason: the script runs as SYSTEM without the
/// agent's client certificate, and the answer is public winget-pkgs metadata. What makes the download safe is
/// checked on the host (the SHA-256 given here, which winget-pkgs recorded; a valid signature; the installed
/// ProductCode), not who asked. nginx's <c>/api/agent-packages</c> block serves it and clears the agent headers.
/// </remarks>
[ApiController]
[Route("api/agent-packages/retired-winget-installer")]
public class RetiredWingetInstallerController : ControllerBase
{
    private readonly IRetiredWingetInstallerLookup _lookup;

    public RetiredWingetInstallerController(IRetiredWingetInstallerLookup lookup)
    {
        _lookup = lookup;
    }

    /// <summary>The installer of <paramref name="version"/> of <paramref name="id"/> whose manifest names
    /// <paramref name="productCode"/>. 404 when none is known; 502 when GitHub could not be asked.</summary>
    [HttpGet]
    [Produces("application/json")]
    [ProducesResponseType(typeof(RetiredWingetInstaller), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status502BadGateway)]
    public async Task<ActionResult<RetiredWingetInstaller>> Get(
        [FromQuery] string? id, [FromQuery] string? version, [FromQuery] string? productCode, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(version) || string.IsNullOrWhiteSpace(productCode))
        {
            return BadRequest();
        }

        try
        {
            return await _lookup.FindAsync(id, version, productCode, cancellationToken) is { } found ? Ok(found) : NotFound();
        }
        catch (Exception ex) when (ex is HttpRequestException || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            // Not "none": the host says GitHub could not be asked, and the next cycle asks again.
            return StatusCode(StatusCodes.Status502BadGateway);
        }
    }
}
