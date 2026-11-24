using System.Text.Json;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Kintsugi.Application.Appearance.Commands.UpdateAppearanceSettings;
using Kintsugi.WebApi.Filters;

namespace Kintsugi.Tests.WebApi;

/// <summary>
/// The route checklist for <c>/api/admin/settings/appearance</c>: both methods carry
/// <c>[RequireAdminSession]</c> (it changes what every administrator sees, and holds an uploaded file),
/// the path is never inside nginx's agent-certificate regex, and nginx proxies it through the general
/// <c>/api</c> location that sits above the SPA fallback rather than answering it with
/// <c>index.html</c>. There is deliberately no anonymous read: a picture on the sign-in screen would need
/// one, and that is a security decision this route does not make (see "Appearance" in src/CLAUDE.md).
/// </summary>
public class AppearanceRouteTests : IClassFixture<ControllerRouteGateTests.RouteTableFactory>
{
    private const string Pattern = "api/admin/settings/appearance";

    private readonly ControllerRouteGateTests.RouteTableFactory _factory;

    public AppearanceRouteTests(ControllerRouteGateTests.RouteTableFactory factory) => _factory = factory;

    [Theory]
    [InlineData("GET")]
    [InlineData("PUT")]
    public void TheRouteCarriesRequireAdminSession(string method)
    {
        var endpoint = _factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Single(e => e.RoutePattern.RawText?.TrimStart('/') == Pattern
                && e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods.Contains(method) == true);

        Assert.NotNull(endpoint.Metadata.GetMetadata<RequireAdminSessionAttribute>());
    }

    [Fact]
    public void NothingAnonymousIsRegisteredUnderThePath()
    {
        var endpoints = _factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Where(e => e.RoutePattern.RawText?.TrimStart('/').StartsWith(Pattern) == true)
            .ToList();

        Assert.Equal(2, endpoints.Count);
        Assert.All(endpoints, e => Assert.NotNull(e.Metadata.GetMetadata<RequireAdminSessionAttribute>()));
    }

    [Fact]
    public void NginxProxiesTheRoute_OutsideTheAgentRegex_AboveTheSpaFallback()
    {
        var config = NginxConfig.Read();

        Assert.DoesNotMatch(NginxConfig.AgentRegex(config), "/" + Pattern);
        Assert.True(NginxConfig.ApiLocationPrecedesSpaFallback(config), "the general /api location must sit above the SPA fallback");
    }

    [Fact]
    public void NginxAllowsABodyBigEnoughForTheWallpaper_AsBase64()
    {
        // 1 MB of picture is about 1.4 MB as base64 inside the JSON; the web client sends exactly that.
        var config = NginxConfig.Read();
        var match = System.Text.RegularExpressions.Regex.Match(config, @"client_max_body_size\s+(\d+)([km]?)");

        Assert.True(match.Success, "nginx sets no client_max_body_size, so its 1m default would refuse the wallpaper");
        var megabytes = match.Groups[2].Value switch { "m" => long.Parse(match.Groups[1].Value), "k" => long.Parse(match.Groups[1].Value) / 1024, _ => 0L };
        Assert.True(megabytes >= 2, $"client_max_body_size is {match.Value}; the wallpaper needs about 1.4 MB");
    }

    // The exact JSON the Flutter client sends (web/lib/data/repositories/appearance_repository_impl.dart),
    // read the way MVC reads a body: web defaults, so camelCase names and a base64 string for a byte[].
    [Fact]
    public void TheJsonTheWebClientSends_BindsIntoTheCommand_WithTheWallpaperDecoded()
    {
        const string body = """
            {"colourMode":"light","presetKey":"gold","accentHex":"#F2B01E","wallpaper":"iVBORw=="}
            """;

        var command = JsonSerializer.Deserialize<UpdateAppearanceSettingsCommand>(
            body, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

        Assert.Equal("light", command.ColourMode);
        Assert.Equal("gold", command.PresetKey);
        Assert.Equal("#F2B01E", command.AccentHex);
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, command.Wallpaper);
    }

    [Fact]
    public void TheJsonTheWebClientSendsWithNoPictureOrColour_BindsNullsNotErrors()
    {
        const string body = """
            {"colourMode":"dark","presetKey":"kintsugi","accentHex":null,"wallpaper":null}
            """;

        var command = JsonSerializer.Deserialize<UpdateAppearanceSettingsCommand>(
            body, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

        Assert.Null(command.AccentHex);
        Assert.Null(command.Wallpaper);
    }
}
