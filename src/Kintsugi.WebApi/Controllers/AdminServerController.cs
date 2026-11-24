using System.Reflection;
using Microsoft.AspNetCore.Mvc;
using Kintsugi.WebApi.Filters;

namespace Kintsugi.WebApi.Controllers;

/// <summary>
/// Facts about this server itself, for the admin UI's shell — today, just its version.
/// </summary>
/// <remarks>
/// <para>
/// Not folded into <c>GET /api/session</c>, which would have been the convenient place: that
/// route is deliberately anonymous, and its own remarks say anything added there is readable by
/// anyone who can reach the server. A build version is exactly the kind of fact that should
/// wait until a caller has signed in, so it lives under <c>/api/admin</c> with the same class-level
/// gate every other browser-driven controller carries.
/// </para>
/// <para>
/// The version is <c>&lt;Version&gt;</c> in <c>Kintsugi.WebApi.csproj</c>, read back from the
/// informational-version attribute the SDK writes from it. The image build stamps it as
/// <c>1.&lt;commit count&gt;+&lt;short sha&gt;</c> through the Dockerfile's <c>SERVER_VERSION</c>
/// build arg, and a build without one says <c>0.0.0-dev</c>. The csproj turns off the SDK's habit of
/// appending <c>+&lt;commit&gt;</c> to that attribute, since the stamped value already names it.
/// </para>
/// </remarks>
[ApiController]
[Route("api/admin/server")]
[Produces("application/json")]
[RequireAdminSession]
public class AdminServerController : ControllerBase
{
    private static readonly string Version =
        typeof(AdminServerController).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(AdminServerController).Assembly.GetName().Version?.ToString(3)
        ?? "unknown";

    [HttpGet]
    [ProducesResponseType(typeof(ServerInfoDto), StatusCodes.Status200OK)]
    public ActionResult<ServerInfoDto> Get() => Ok(new ServerInfoDto(Version));
}

/// <param name="Version">This server build's version, as the image build stamped it into
/// <c>Kintsugi.WebApi.csproj</c>. Read by
/// <c>web/lib/data/repositories/server_info_repository_impl.dart</c>.</param>
public record ServerInfoDto(string Version);
