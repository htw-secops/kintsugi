using Microsoft.AspNetCore.Mvc;
using Kintsugi.Application.Common.Interfaces;

namespace Kintsugi.WebApi.Controllers;

/// <summary>
/// Kintsugi's copy of App Installer, fetched by the winget script on a host whose Microsoft Store cannot
/// update winget (<c>WingetUpgradeScript</c>'s <c>Install-AppInstallerFromKintsugi</c>). Filled by
/// <c>AppInstallerMirrorBackgroundService</c>.
/// </summary>
/// <remarks>
/// Anonymous, like the agent-package reads beside it and for the same reason: the files are Microsoft's public
/// release, and what makes them safe to install is checked on the host (the SHA-256 listed here, and a valid
/// Microsoft Corporation signature on every package), not who asked. The script runs as SYSTEM without the
/// agent's client certificate. nginx streams the files from its own location without spooling them to disk;
/// see nginx/default.conf.
/// </remarks>
[ApiController]
[Route("api/agent-packages/app-installer")]
public class AppInstallerController : ControllerBase
{
    private readonly IAppInstallerMirror _mirror;

    public AppInstallerController(IAppInstallerMirror mirror)
    {
        _mirror = mirror;
    }

    /// <summary>The version on offer and its files, each with its SHA-256. 404 until the first copy is made.</summary>
    [HttpGet]
    [Produces("application/json")]
    [ProducesResponseType(typeof(AppInstallerOffer), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<AppInstallerOffer>> GetOffer(CancellationToken cancellationToken) =>
        await _mirror.GetOfferAsync(cancellationToken) is { } offer ? Ok(offer) : NotFound();

    /// <summary>One file the offer lists; any other name is a 404.</summary>
    [HttpGet("files/{fileName}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetFile(string fileName, CancellationToken cancellationToken) =>
        await _mirror.OpenFileAsync(fileName, cancellationToken) is { } stream
            ? File(stream, "application/octet-stream", fileName)
            : NotFound();
}
