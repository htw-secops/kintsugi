using System.Diagnostics;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Kintsugi.Application.AiSettings;
using Kintsugi.Application.Common.Exceptions;
using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Domain.Enums;

namespace Kintsugi.Infrastructure.Ai.Engine;

/// <summary>
/// One call to a model, whichever protocol reaches it: picks the adapter, decides how the model
/// gets to the web, and runs the tool loop.
/// </summary>
/// <remarks>
/// <para>
/// <b>Web access is decided here, once, for every protocol.</b> Research prefers the provider's
/// own hosted search when the connection allows it and the protocol has one (Anthropic, OpenAI
/// Responses, Gemini grounding). Otherwise, when a <see cref="WebSearchBackend"/> is configured, it
/// runs on Kintsugi's own <c>web_search</c> and <c>web_fetch</c> tools — the loop Ollama used to
/// have alone, now under every protocol. With neither, the model is told it has no live web access
/// and to say so in the script, which is what Ollama without a search key always did. Hosted
/// search and Kintsugi's tools are never offered together: Gemini refuses the mixture, and one
/// rule for all is easier to reason about than a per-protocol exception.
/// </para>
/// <para>
/// No tools at all are offered when there is no search backend, rather than <c>web_fetch</c>
/// alone: a local model without tool support answers a request carrying tools with an error, and
/// that was a working configuration before this engine existed.
/// </para>
/// </remarks>
public class AiEngine : IAiConnectionProbe
{
    /// <summary>Rounds of model → tools → model before the model is asked to answer with what it
    /// has. Research needs two or three; this is the ceiling against a model that never stops.</summary>
    public const int MaxToolRounds = 6;

    public const int MaxToolCalls = 12;

    internal const string WithSearchSuffix =
        "\n\nUse the web_search tool to confirm the current version and download/release-notes " +
        "URL before answering — don't rely on training data alone for those. Use web_fetch to read " +
        "a specific page, such as a release page or a repository's latest-release redirect.";

    internal const string WithoutSearchSuffix =
        "\n\nNote: you do not have live web access for this request — write your best script " +
        "anyway, using what you already know, but add a \"# WARNING: ...\" comment near the top " +
        "explaining that the version or URL details may be out of date.";

    private static readonly JsonObject QueryParameters = new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject { ["query"] = new JsonObject { ["type"] = "string", ["description"] = "The search query." } },
        ["required"] = new JsonArray("query"),
    };

    private static readonly JsonObject UrlParameters = new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject { ["url"] = new JsonObject { ["type"] = "string", ["description"] = "The absolute http(s) URL to read." } },
        ["required"] = new JsonArray("url"),
    };

    private readonly IReadOnlyDictionary<AiWireProtocol, IAiProtocolAdapter> _adapters;
    private readonly WebSearchClient _webSearch;
    private readonly SafeWebFetcher _fetcher;
    private readonly ILogger<AiEngine> _logger;
    private readonly AiProviderMemory _memory;

    public AiEngine(
        IEnumerable<IAiProtocolAdapter> adapters, WebSearchClient webSearch, SafeWebFetcher fetcher, ILogger<AiEngine> logger,
        AiProviderMemory? memory = null)
    {
        _memory = memory ?? AiProviderMemory.Shared;
        _adapters = adapters.ToDictionary(a => a.Protocol);
        _webSearch = webSearch;
        _fetcher = fetcher;
        _logger = logger;
    }

    /// <summary>Research: the prompt, with whatever web access the connection and server allow.</summary>
    /// <remarks>
    /// When the provider refuses hosted web search (an organisation policy, an unsupported tool) the
    /// request is retried once without it, on the route's other web options, and hosted search is
    /// not offered again on that connection and model for five minutes
    /// (<see cref="AiProviderMemory.DefaultLatchFor"/>), or until Test succeeds on it. A refusal that is
    /// about the configuration itself is remembered for a few minutes, so the rest of a scan fails
    /// at once with the same message instead of asking the provider once per application.
    /// </remarks>
    public Task<string> ResearchAsync(AiRoute route, AiWebSearchSettings search, string prompt, int? maxOutputTokens, CancellationToken cancellationToken) =>
        GuardedAsync(route, async () =>
        {
            var hosted = route.Connection.UseHostedWebSearch && HasHostedSearch(route.Connection.Protocol)
                && !_memory.IsHostedSearchRejected(route);
            if (hosted)
            {
                try
                {
                    return await RunAsync(route, search, prompt, tools: [], useHosted: true, maxOutputTokens, cancellationToken);
                }
                catch (AiProviderMisconfiguredException ex) when (ex.RejectsHostedWebSearch)
                {
                    _memory.RejectHostedSearch(route);
                    _logger.LogWarning(
                        "The AI provider rejected hosted web search on connection {Connection} for model {Model}; retrying without it and not offering it again for a few minutes (or until a successful Test). "
                        + "Fix: allow it in the org policy constraints/vertexai.allowedPartnerModelFeatures, or turn off hosted search on the connection.",
                        route.Connection.Name, route.Model);
                }
            }

            if (search.IsConfigured)
            {
                return await RunAsync(route, search, prompt + WithSearchSuffix, KintsugiTools, useHosted: false, maxOutputTokens, cancellationToken);
            }

            return await RunAsync(route, search, prompt + WithoutSearchSuffix, tools: [], useHosted: false, maxOutputTokens, cancellationToken);
        });

    /// <summary>A plain prompt-in, text-out call: no tools, no search.</summary>
    public Task<string> CompleteAsync(AiRoute route, string prompt, int? maxOutputTokens, CancellationToken cancellationToken) =>
        GuardedAsync(route, () => CompleteCoreAsync(route, prompt, maxOutputTokens, cancellationToken));

    private Task<string> CompleteCoreAsync(AiRoute route, string prompt, int? maxOutputTokens, CancellationToken cancellationToken) =>
        RunAsync(route, AiWebSearchSettings.None, prompt, tools: [], useHosted: false, maxOutputTokens, cancellationToken);

    /// <summary>Refuses at once, with the remembered message, a route whose configuration the provider
    /// has just refused; otherwise runs the call and remembers (or forgets) the outcome.</summary>
    private async Task<string> GuardedAsync(AiRoute route, Func<Task<string>> call)
    {
        if (_memory.Latched(route) is { } known)
        {
            throw known.Again();
        }

        try
        {
            var text = await call();
            _memory.Clear(route);
            return text;
        }
        catch (AiProviderMisconfiguredException ex)
        {
            _memory.Latch(route, ex);
            _logger.LogWarning(
                "AI provider misconfigured on connection {Connection}, model {Model}: {Detail} (provider said: {ProviderDetail})",
                route.Connection.Name, route.Model, ex.Detail, ex.ProviderDetail);
            throw;
        }
    }

    public async Task<AiConnectionTestResultDto> ProbeAsync(AiConnectionSettings connection, string model, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            // Not through the remembered refusal: the Test button is how a fix is confirmed, so it
            // always asks the provider, and a success makes the next real call try again too.
            var route = new AiRoute(connection, model);
            var reply = await CompleteCoreAsync(route, "Reply with the single word OK.", 64, cancellationToken);
            _memory.Clear(route);
            // The person pressing Test is checking a fix: let the next research try web search again.
            _memory.ClearHostedSearchRejection(route);
            return new AiConnectionTestResultDto(true, AiHttp.Truncate(reply.Trim(), 200), null, stopwatch.ElapsedMilliseconds);
        }
        catch (ExternalServiceException ex)
        {
            return new AiConnectionTestResultDto(false, null, ex.Message, stopwatch.ElapsedMilliseconds);
        }
    }

    internal static bool HasHostedSearch(AiWireProtocol protocol) =>
        protocol is AiWireProtocol.Anthropic or AiWireProtocol.OpenAiResponses or AiWireProtocol.Google;

    private static readonly IReadOnlyList<AiToolDefinition> KintsugiTools =
    [
        new("web_search", "Search the public web for current information and return matching pages.", QueryParameters),
        new("web_fetch", "Fetch a public web page and return its readable text.", UrlParameters),
    ];

    private async Task<string> RunAsync(
        AiRoute route, AiWebSearchSettings search, string prompt, IReadOnlyList<AiToolDefinition> tools, bool useHosted,
        int? maxOutputTokens, CancellationToken cancellationToken)
    {
        if (!_adapters.TryGetValue(route.Connection.Protocol, out var adapter))
        {
            throw new ExternalServiceException($"No adapter speaks {route.Connection.Protocol}.");
        }

        var messages = new List<AiChatMessage> { AiChatMessage.User(prompt) };
        var toolCallsMade = 0;

        for (var round = 0; ; round++)
        {
            // Out of budget: one last call with no tools, so the model answers with what it has
            // rather than the loop ending on a tool call nobody will answer.
            var offerTools = round < MaxToolRounds && toolCallsMade < MaxToolCalls ? tools : [];
            var result = await adapter.SendAsync(
                route.Connection,
                new AiChatRequest(route.Model, messages, offerTools, useHosted, maxOutputTokens),
                cancellationToken);

            // Counted for the budget by whoever opened an AiUsageScope; nothing here if none is open.
            if (result.Usage is { } usage)
            {
                AiUsageScope.Add(new AiUsageEntry(
                    route.Connection.Name, route.Model, usage.InputTokens, usage.OutputTokens,
                    Free: route.Connection.Protocol == AiWireProtocol.Ollama));
            }

            // After the usage above, because the tokens were spent either way. A cut-off reply is not an
            // answer: a script stopped mid-line reaches the validator as shellcheck's opinion of half a
            // line, and an empty one as "no usable script", and neither names the cause.
            if (result.HitOutputLimit)
            {
                throw new ExternalServiceException(
                    $"The AI provider ({route.Connection.Name}) stopped at its output limit after {result.Usage?.OutputTokens.ToString() ?? "an unknown number of"} tokens, " +
                    "before it finished its answer, so the reply is incomplete.");
            }

            if (result.ToolCalls.Count == 0 && string.IsNullOrWhiteSpace(result.Text))
            {
                // Logged, not thrown: callers already treat an empty answer as one, and which stop
                // reason came with it is what tells a refusal from a paused search turn.
                _logger.LogWarning(
                    "AI call via {Connection} ({Model}) returned no text; stop reason {StopReason}",
                    route.Connection.Name, route.Model, result.StopReason ?? "none given");
            }

            if (result.ToolCalls.Count == 0 || offerTools.Count == 0)
            {
                return result.Text;
            }

            messages.Add(result.Assistant);
            foreach (var call in result.ToolCalls)
            {
                toolCallsMade++;
                var output = await ExecuteToolAsync(call, search, cancellationToken);
                _logger.LogInformation(
                    "AI tool call via {Connection}: {Tool}({Arguments}) → {Length} chars",
                    route.Connection.Name, call.Name, call.Arguments.ToJsonString(), output.Length);
                messages.Add(AiChatMessage.ToolResult(call, output));
            }
        }
    }

    private async Task<string> ExecuteToolAsync(AiToolCall call, AiWebSearchSettings search, CancellationToken cancellationToken)
    {
        switch (call.Name)
        {
            case "web_search":
                var query = call.Arguments["query"]?.ToString();
                return string.IsNullOrWhiteSpace(query)
                    ? "web_search needs a 'query' argument."
                    : await _webSearch.SearchAsync(search, query, cancellationToken);

            case "web_fetch":
                var url = call.Arguments["url"]?.ToString();
                return string.IsNullOrWhiteSpace(url)
                    ? "web_fetch needs a 'url' argument."
                    : await _fetcher.FetchTextAsync(url, cancellationToken);

            default:
                return $"There is no tool called '{call.Name}'. The tools are web_search and web_fetch.";
        }
    }
}
