using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Kintsugi.Application.AiSettings;
using Kintsugi.Domain.Enums;

namespace Kintsugi.Infrastructure.Ai.Engine;

/// <summary>
/// Turns a provider's error response into a configuration finding when it is one. A 404 for the
/// model, an organisation-policy refusal and a rejected credential are not about the application
/// being researched — every application would fail the same way — so they are named once, in plain
/// words, instead of surfacing as one raw HTTP dump per application.
/// </summary>
public static class AiProviderErrors
{
    private static readonly Regex Constraint = new(@"constraints/[A-Za-z0-9_.\-]+", RegexOptions.Compiled);
    private static readonly Regex OrgPolicy = new(@"org(anization|anisation)?(al)?[\s_-]*polic", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Null when the response is not a configuration problem (a 429, a 5xx, a malformed
    /// request body…): the caller then reports it as it always has.</summary>
    public static AiProviderMisconfiguredException? Classify(AiConnectionSettings connection, string? model, int status, string body)
    {
        var where = Describe(connection);
        var modelText = string.IsNullOrWhiteSpace(model) ? "(none)" : model;
        var raw = AiHttp.Truncate(body ?? string.Empty, 500);

        var policyConstraint = Constraint.Match(body ?? string.Empty);
        var mentionsPolicy = policyConstraint.Success || OrgPolicy.IsMatch(body ?? string.Empty);
        if (status is 400 or 403 && mentionsPolicy)
        {
            var rejectsSearch = (body ?? string.Empty).Contains("web_search", StringComparison.OrdinalIgnoreCase)
                || (body ?? string.Empty).Contains("allowedPartnerModelFeatures", StringComparison.OrdinalIgnoreCase);
            var policy = policyConstraint.Success ? policyConstraint.Value : "an organisation policy";
            var detail = rejectsSearch
                ? $"the policy {policy} blocks web_search for model '{modelText}' on connection '{connection.Name}' ({where}). "
                    + "Either allow it for that model in the org policy constraints/vertexai.allowedPartnerModelFeatures, or turn off 'hosted search' on the connection. "
                    + "Scripts are still written without web search in the meantime. Once the policy is fixed, web search is tried again by itself within a few minutes, "
                    + "or at once after a successful Test on the connection (Test sends a plain prompt, so it confirms the connection and model answer, not that web search is allowed)."
                : $"the policy {policy} refused the request for model '{modelText}' on connection '{connection.Name}' ({where}). "
                    + "Ask whoever owns the organisation policy to allow it, then use Test on the connection to re-check.";
            return new AiProviderMisconfiguredException(AiProviderMisconfigurationKind.PolicyViolation, connection.Name, model, detail, rejectsSearch, raw);
        }

        if (status == 404)
        {
            var detail = $"model '{modelText}' was not found on connection '{connection.Name}' ({where}), or the endpoint address is wrong. "
                + "Check the model id on the route and that the model is enabled for that project and location, then use Test on the connection to re-check.";
            return new AiProviderMisconfiguredException(AiProviderMisconfigurationKind.ModelNotFound, connection.Name, model, detail, providerDetail: raw);
        }

        if (status is 401 or 403)
        {
            var credential = connection.AuthMode == AiAuthMode.GoogleCloud
                ? "the Google Cloud identity this server runs as needs access to Vertex AI in that project"
                : "check the API key";
            var detail = $"connection '{connection.Name}' ({where}) was refused with HTTP {status}: {credential}. Use Test on the connection to re-check.";
            return new AiProviderMisconfiguredException(AiProviderMisconfigurationKind.AuthFailed, connection.Name, model, detail, providerDetail: raw);
        }

        return null;
    }

    /// <summary>Where a connection points, without any secret: the Vertex project and location, or
    /// the host of its base URL.</summary>
    public static string Describe(AiConnectionSettings connection)
    {
        if (connection.AuthMode == AiAuthMode.GoogleCloud)
        {
            return $"Vertex AI project {Or(connection.GoogleCloudProject)}, location {Or(connection.GoogleCloudLocation)}";
        }

        return connection.BaseUrl is not null && Uri.TryCreate(connection.BaseUrl, UriKind.Absolute, out var uri)
            ? $"endpoint {uri.Host}"
            : $"{connection.Protocol} default endpoint";

        static string Or(string? value) => string.IsNullOrWhiteSpace(value) ? "(not set)" : value;
    }
}

/// <summary>
/// What the engine has learned about a (connection, model) while the process runs: that its
/// configuration is refused (so further calls in the same scan fail at once with the same message,
/// without another round trip each), and that the provider rejects hosted web search (so it is not
/// offered again). Forgotten on restart, and a successful call or a Test clears the first.
/// </summary>
public class AiProviderMemory
{
    /// <summary>How long a configuration refusal is remembered. Long enough to span one scan, short
    /// enough that a fix is noticed without a restart.</summary>
    public static readonly TimeSpan DefaultLatchFor = TimeSpan.FromMinutes(5);

    public static readonly AiProviderMemory Shared = new();

    private readonly ConcurrentDictionary<string, (AiProviderMisconfiguredException Error, DateTimeOffset At)> _latched = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> _hostedRejected = new();
    private readonly Func<DateTimeOffset> _now;
    private readonly TimeSpan _latchFor;

    public AiProviderMemory(Func<DateTimeOffset>? now = null, TimeSpan? latchFor = null)
    {
        _now = now ?? (() => DateTimeOffset.UtcNow);
        _latchFor = latchFor ?? DefaultLatchFor;
    }

    private static string Key(AiRoute route) => $"{route.Connection.Name}|{route.Model}".ToLowerInvariant();

    /// <summary>The refusal still in memory for this route, or null.</summary>
    public AiProviderMisconfiguredException? Latched(AiRoute route)
    {
        if (_latched.TryGetValue(Key(route), out var entry))
        {
            if (_now() - entry.At < _latchFor)
            {
                return entry.Error;
            }

            _latched.TryRemove(Key(route), out _);
        }

        return null;
    }

    public void Latch(AiRoute route, AiProviderMisconfiguredException error) => _latched[Key(route)] = (error, _now());

    /// <summary>Forgets the refusal remembered for a route. Every successful call does this.</summary>
    public void Clear(AiRoute route) => _latched.TryRemove(Key(route), out _);

    /// <summary>Forgets that hosted web search was rejected for a route. Only a successful Test does this, and not
    /// every success, because the rejection stands while ordinary calls succeed without web search: a plain
    /// success says nothing about whether the policy that blocked it has been fixed.</summary>
    public void ClearHostedSearchRejection(AiRoute route) => _hostedRejected.TryRemove(Key(route), out _);

    /// <summary>Whether hosted web search was rejected for this route recently. It expires like the refusal does
    /// (<see cref="DefaultLatchFor"/>): the policy that blocked it can be fixed at any time, and a rejection
    /// remembered until the API restarted ran research without web search with nothing on screen saying so.</summary>
    public bool IsHostedSearchRejected(AiRoute route)
    {
        if (_hostedRejected.TryGetValue(Key(route), out var at))
        {
            if (_now() - at < _latchFor)
            {
                return true;
            }

            _hostedRejected.TryRemove(Key(route), out _);
        }

        return false;
    }

    public void RejectHostedSearch(AiRoute route) => _hostedRejected[Key(route)] = _now();
}
