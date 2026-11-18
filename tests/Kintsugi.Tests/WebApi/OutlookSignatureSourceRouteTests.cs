using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Kintsugi.WebApi.Filters;

namespace Kintsugi.Tests.WebApi;

/// <summary>
/// The route checklist for <c>/api/admin/outlook-signature-source</c>: it carries
/// <c>[RequireAdminSession]</c> (it decides where this server sends a token for its own identity), it is
/// never inside nginx's agent-certificate regex, and nginx proxies it through the general <c>/api</c>
/// location that sits above the SPA fallback rather than answering it with <c>index.html</c>.
/// </summary>
public class OutlookSignatureSourceRouteTests : IClassFixture<ControllerRouteGateTests.RouteTableFactory>
{
    private const string Pattern = "api/admin/outlook-signature-source";

    private readonly ControllerRouteGateTests.RouteTableFactory _factory;

    public OutlookSignatureSourceRouteTests(ControllerRouteGateTests.RouteTableFactory factory) => _factory = factory;

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
    public void NginxProxiesTheRoute_OutsideTheAgentRegex_AboveTheSpaFallback()
    {
        var config = NginxConfig.Read();

        Assert.DoesNotMatch(NginxConfig.AgentRegex(config), "/" + Pattern);
        Assert.True(NginxConfig.ApiLocationPrecedesSpaFallback(config), "the general /api location must sit above the SPA fallback");
    }
}

/// <summary>Reads <c>nginx/default.conf</c> the way the route tests need it.</summary>
internal static class NginxConfig
{
    public static string Read()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Kintsugi.sln")))
        {
            directory = directory.Parent;
        }

        return File.ReadAllText(Path.Combine(directory!.FullName, "nginx", "default.conf"));
    }

    /// <summary>The agent-certificate location's regex, compiled as nginx's <c>~*</c> reads it.</summary>
    public static Regex AgentRegex(string config)
    {
        var match = Regex.Match(config, @"location ~\* (\^/api/\(host\|[^ ]+) \{");
        Assert.True(match.Success, "the agent-certificate location was not found in nginx/default.conf");
        return new Regex(match.Groups[1].Value, RegexOptions.IgnoreCase);
    }

    /// <summary>Whether a plain <c>location /api {</c> exists before the last <c>location / {</c>, the SPA fallback.</summary>
    public static bool ApiLocationPrecedesSpaFallback(string config)
    {
        var api = config.IndexOf("location /api {", StringComparison.Ordinal);
        var spa = config.LastIndexOf("location / {", StringComparison.Ordinal);
        return api >= 0 && spa > api;
    }
}
