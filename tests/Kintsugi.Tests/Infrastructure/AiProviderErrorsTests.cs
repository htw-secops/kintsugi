using System.Net;
using System.Text.Json.Nodes;
using Kintsugi.Application.AiSettings;
using Kintsugi.Application.Common.Exceptions;
using Kintsugi.Domain.Enums;
using Kintsugi.Infrastructure.Ai.Engine;

namespace Kintsugi.Tests.Infrastructure;

public class AiProviderErrorsTests
{
    // The two failures seen on the live fleet, as Vertex words them.
    private const string HaikuNotFound =
        """{"error":{"code":404,"message":"Publisher Model `projects/example-project-123456/locations/global/publishers/anthropic/models/claude-haiku-5-5@default` not found.","status":"NOT_FOUND"}}""";

    private const string WebSearchPolicy =
        """{"error":{"code":400,"message":"Request violates constraint 'constraints/vertexai.allowedPartnerModelFeatures': the feature web_search is not allowed for model claude-sonnet-5-5 in project 123456789012.","status":"FAILED_PRECONDITION"}}""";

    internal static AiConnectionSettings Vertex(string name = "Vertex - Anthropic", bool hosted = true) =>
        new(name, AiWireProtocol.Anthropic, null, AiAuthMode.GoogleCloud, null, "example-project-123456", "global", hosted);

    [Fact]
    public void A404_ForTheHaikuModel_IsModelNotFound_NamingTheConnectionModelProjectAndLocation()
    {
        var error = AiProviderErrors.Classify(Vertex(), "claude-haiku-5-5@default", 404, HaikuNotFound);

        Assert.NotNull(error);
        Assert.Equal(AiProviderMisconfigurationKind.ModelNotFound, error!.Kind);
        Assert.StartsWith("AI provider misconfigured: ", error.Message);
        Assert.Contains("claude-haiku-5-5@default", error.Message);
        Assert.Contains("Vertex - Anthropic", error.Message);
        Assert.Contains("example-project-123456", error.Message);
        Assert.Contains("global", error.Message);
        // The provider's own JSON is for the log, never the message an operator reads per application.
        Assert.DoesNotContain("Publisher Model", error.Message);
        Assert.Contains("Publisher Model", error.ProviderDetail);
    }

    [Fact]
    public void A400_NamingTheWebSearchPolicy_IsAPolicyViolation_ThatRejectsHostedWebSearch()
    {
        var error = AiProviderErrors.Classify(Vertex(), "claude-sonnet-5-5@default", 400, WebSearchPolicy);

        Assert.NotNull(error);
        Assert.Equal(AiProviderMisconfigurationKind.PolicyViolation, error!.Kind);
        Assert.True(error.RejectsHostedWebSearch);
        Assert.Contains("constraints/vertexai.allowedPartnerModelFeatures", error.Message);
        Assert.Contains("blocks web_search for model 'claude-sonnet-5-5@default'", error.Message);
        // Both ways out are named.
        Assert.Contains("turn off 'hosted search'", error.Message);
        // The project number Google prints is not shown as if it were a second project.
        Assert.DoesNotContain("123456789012", error.Message);
    }

    [Fact]
    public void AnotherPolicy_IsAPolicyViolation_ButDoesNotBlameWebSearch()
    {
        var error = AiProviderErrors.Classify(
            Vertex(), "m", 400, """{"error":{"message":"Constraint constraints/gcp.resourceLocations violated","status":"FAILED_PRECONDITION"}}""");

        Assert.Equal(AiProviderMisconfigurationKind.PolicyViolation, error!.Kind);
        Assert.False(error.RejectsHostedWebSearch);
        Assert.Contains("constraints/gcp.resourceLocations", error.Message);
    }

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    public void AnAuthRefusal_IsAuthFailed(int status)
    {
        var error = AiProviderErrors.Classify(Vertex(), "m", status, """{"error":{"message":"Permission denied"}}""");

        Assert.Equal(AiProviderMisconfigurationKind.AuthFailed, error!.Kind);
        Assert.Contains($"HTTP {status}", error.Message);
    }

    [Theory]
    [InlineData(400, "{\"error\":{\"message\":\"messages: Input should be a valid list\"}}")]
    [InlineData(429, "rate limited")]
    [InlineData(500, "boom")]
    [InlineData(503, "unavailable")]
    public void EverythingElse_IsNotAConfigurationProblem(int status, string body)
    {
        Assert.Null(AiProviderErrors.Classify(Vertex(), "m", status, body));
    }

    [Fact]
    public void NoSecretIsEverPartOfTheMessage()
    {
        var connection = new AiConnectionSettings("Direct", AiWireProtocol.Anthropic, "https://api.anthropic.com/v1", AiAuthMode.ApiKey, "sk-ant-SECRET", null, null, true);

        var error = AiProviderErrors.Classify(connection, "claude", 401, "invalid x-api-key");

        Assert.DoesNotContain("sk-ant-SECRET", error!.Message);
        Assert.Contains("api.anthropic.com", error.Message);
    }

    // --- through the engine ------------------------------------------------------------------

    private static bool IsVertex(HttpRequestMessage r) => r.RequestUri!.Host == "aiplatform.googleapis.com";

    private static RoutingHandler WithToken() =>
        new RoutingHandler().OnJson(r => r.RequestUri!.Host == "metadata.google.internal", _ => new { access_token = "ya29.token", expires_in = 3600 });

    private static object Answer(string text) => new { content = new[] { new { type = "text", text } }, usage = new { input_tokens = 10, output_tokens = 2 } };

    private static HttpResponseMessage Error(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };

    [Fact]
    public async Task WhenVertexRejectsWebSearch_TheRequestIsRetriedOnceWithoutIt_AndTheAnswerIsReturned()
    {
        var handler = WithToken().On(IsVertex, (_, body) =>
            body.Contains("web_search_20250305")
                ? Error(HttpStatusCode.BadRequest, WebSearchPolicy)
                : RoutingHandler.Json(Answer("the script")));
        var engine = TestAiEngine.Over(handler);
        var route = new AiRoute(Vertex("fallback-1"), "claude-sonnet-5-5@default");

        var text = await engine.ResearchAsync(route, AiWebSearchSettings.None, "write it", null, CancellationToken.None);

        Assert.Equal("the script", text);
        var sent = handler.Calls.Where(c => IsVertex(c.Request)).Select(c => c.Body).ToList();
        Assert.Equal(2, sent.Count);
        Assert.Contains("web_search_20250305", sent[0]);
        Assert.DoesNotContain("web_search_20250305", sent[1]);
        // Told it has no live web access, as when no search is available at all.
        Assert.Contains("do not have live web access", sent[1]);
    }

    [Fact]
    public async Task OnceWebSearchIsRejected_LaterRequestsOnThatConnectionDoNotTryItAgain()
    {
        var handler = WithToken().On(IsVertex, (_, body) =>
            body.Contains("web_search_20250305")
                ? Error(HttpStatusCode.BadRequest, WebSearchPolicy)
                : RoutingHandler.Json(Answer("ok")));
        var engine = TestAiEngine.Over(handler);
        var route = new AiRoute(Vertex("fallback-2"), "claude-sonnet-5-5@default");

        await engine.ResearchAsync(route, AiWebSearchSettings.None, "one", null, CancellationToken.None);
        handler.Calls.Clear();
        await engine.ResearchAsync(route, AiWebSearchSettings.None, "two", null, CancellationToken.None);

        var sent = handler.Calls.Where(c => IsVertex(c.Request)).ToList();
        Assert.Single(sent);
        Assert.DoesNotContain("web_search_20250305", sent[0].Body);
    }

    [Fact]
    public async Task AWebSearchFallback_IsNotAConfigurationFailure_SoNothingIsRemembered()
    {
        var memory = new AiProviderMemory();
        var handler = WithToken().On(IsVertex, (_, body) =>
            body.Contains("web_search_20250305") ? Error(HttpStatusCode.BadRequest, WebSearchPolicy) : RoutingHandler.Json(Answer("ok")));
        var route = new AiRoute(Vertex("fallback-3"), "m");

        await TestAiEngine.Over(handler, memory: memory).ResearchAsync(route, AiWebSearchSettings.None, "p", null, CancellationToken.None);

        Assert.Null(memory.Latched(route));
    }

    [Fact]
    public async Task AModelNotFound_FailsOnce_AndTheNextCallsAreRefusedWithTheSameMessageWithoutARequest()
    {
        var handler = WithToken().On(IsVertex, (_, _) => Error(HttpStatusCode.NotFound, HaikuNotFound));
        var engine = TestAiEngine.Over(handler);
        var route = new AiRoute(Vertex("latch-1"), "claude-haiku-5-5@default");

        var first = await Assert.ThrowsAsync<AiProviderMisconfiguredException>(
            () => engine.CompleteAsync(route, "a", null, CancellationToken.None));
        var requestsAfterFirst = handler.Calls.Count(c => IsVertex(c.Request));

        var second = await Assert.ThrowsAsync<AiProviderMisconfiguredException>(
            () => engine.ResearchAsync(route, AiWebSearchSettings.None, "b", null, CancellationToken.None));

        Assert.Equal(1, requestsAfterFirst);
        Assert.Equal(requestsAfterFirst, handler.Calls.Count(c => IsVertex(c.Request)));
        Assert.Equal(first.Message, second.Message);
    }

    [Fact]
    public async Task ARemembered_Refusal_ExpiresAfterTheLatchPeriod_SoAFixIsNoticed()
    {
        var clock = new FakeClock();
        var memory = new AiProviderMemory(clock.Now, TimeSpan.FromMinutes(5));
        var calls = 0;
        var handler = WithToken().On(IsVertex, (_, _) =>
        {
            calls++;
            return calls == 1 ? Error(HttpStatusCode.NotFound, HaikuNotFound) : RoutingHandler.Json(Answer("fixed"));
        });
        var engine = TestAiEngine.Over(handler, memory: memory);
        var route = new AiRoute(Vertex("latch-2"), "m");

        await Assert.ThrowsAsync<AiProviderMisconfiguredException>(() => engine.CompleteAsync(route, "a", null, CancellationToken.None));
        clock.Advance(TimeSpan.FromMinutes(4));
        await Assert.ThrowsAsync<AiProviderMisconfiguredException>(() => engine.CompleteAsync(route, "a", null, CancellationToken.None));
        Assert.Equal(1, calls);

        clock.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal("fixed", await engine.CompleteAsync(route, "a", null, CancellationToken.None));
        Assert.Null(memory.Latched(route));
    }

    [Fact]
    public async Task TheTestButton_AlwaysAsksTheProvider_AndASuccessClearsTheMemory()
    {
        var memory = new AiProviderMemory();
        var route = new AiRoute(Vertex("latch-3"), "m");
        memory.Latch(route, AiProviderErrors.Classify(route.Connection, "m", 404, HaikuNotFound)!);
        var handler = WithToken().On(IsVertex, (_, _) => RoutingHandler.Json(Answer("OK")));
        var engine = TestAiEngine.Over(handler, memory: memory);

        var result = await engine.ProbeAsync(route.Connection, "m", CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Null(memory.Latched(route));
    }

    [Fact]
    public async Task ASuccessfulTest_AlsoForgetsThatHostedWebSearchWasRejected()
    {
        // Test sends a plain prompt, so it cannot show that web search is now allowed; but a success is how a person
        // says "I fixed it", and the rejection must not outlive that until the API restarts.
        var memory = new AiProviderMemory();
        var route = new AiRoute(Vertex("latch-6"), "m");
        memory.RejectHostedSearch(route);
        Assert.True(memory.IsHostedSearchRejected(route));
        var handler = WithToken().On(IsVertex, (_, _) => RoutingHandler.Json(Answer("OK")));

        var result = await TestAiEngine.Over(handler, memory: memory).ProbeAsync(route.Connection, "m", CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.False(memory.IsHostedSearchRejected(route));
    }

    [Fact]
    public void ARejectionOfHostedWebSearch_ExpiresAfterTheLatchPeriod_SoAPolicyFixIsNoticedWithoutARestart()
    {
        var clock = new FakeClock();
        var memory = new AiProviderMemory(clock.Now, TimeSpan.FromMinutes(5));
        var route = new AiRoute(Vertex("latch-7"), "m");

        memory.RejectHostedSearch(route);
        clock.Advance(TimeSpan.FromMinutes(4));
        Assert.True(memory.IsHostedSearchRejected(route));

        clock.Advance(TimeSpan.FromMinutes(2));
        Assert.False(memory.IsHostedSearchRejected(route));
    }

    [Fact]
    public void TheWebSearchPolicyMessage_DoesNotClaimThatTestRechecksWebSearch()
    {
        var error = AiProviderErrors.Classify(Vertex("latch-8"), "m", 400, WebSearchPolicy)!;

        Assert.Contains("plain prompt", error.Message);
        Assert.Contains("within a few minutes", error.Message);
        Assert.DoesNotContain("Use Test on the connection to re-check", error.Message);
    }

    [Fact]
    public async Task TheTestButton_ReportsTheSingleClearMessage()
    {
        var handler = WithToken().On(IsVertex, (_, _) => Error(HttpStatusCode.NotFound, HaikuNotFound));

        var result = await TestAiEngine.Over(handler).ProbeAsync(Vertex("latch-4"), "claude-haiku-5-5@default", CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.StartsWith("AI provider misconfigured: ", result.Error);
    }

    [Fact]
    public async Task AnOrdinaryServerError_StaysAnOrdinaryExternalServiceError_AndIsNotRemembered()
    {
        var memory = new AiProviderMemory();
        var handler = WithToken().On(IsVertex, (_, _) => Error(HttpStatusCode.ServiceUnavailable, "try later"));
        var route = new AiRoute(Vertex("latch-5"), "m");

        var ex = await Assert.ThrowsAsync<ExternalServiceException>(
            () => TestAiEngine.Over(handler, memory: memory).CompleteAsync(route, "a", null, CancellationToken.None));

        Assert.IsNotType<AiProviderMisconfiguredException>(ex);
        Assert.Contains("HTTP 503", ex.Message);
        Assert.Null(memory.Latched(route));
    }

    private sealed class FakeClock
    {
        private DateTimeOffset _now = new(2026, 11, 16, 9, 0, 0, TimeSpan.Zero);
        public DateTimeOffset Now() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }
}

public class AiConfigValidatorTests
{
    private static AiConnectionSettings Vertex(string name, string? project, string? location) =>
        new(name, AiWireProtocol.Anthropic, null, AiAuthMode.GoogleCloud, null, project, location, true);

    private static AiConfigRoute Route(AiFeature feature, AiConnectionSettings connection, string model = "claude-sonnet-5-5@default") =>
        new(feature, connection, model);

    [Fact]
    public void TheLiveSetup_OneVertexProjectWithValidModels_HasNoIssues()
    {
        var connection = Vertex("Vertex - Anthropic", "example-project-123456", "global");

        var issues = AiConfigValidator.Validate(
        [
            Route(AiFeature.ScriptResearch, connection, "claude-opus-5-5@default"),
            Route(AiFeature.ScriptRepair, connection),
            Route(AiFeature.CpeSuggestion, connection),
        ]);

        Assert.Empty(issues);
    }

    [Fact]
    public void AVertexConnection_WithNoProjectOrLocation_IsAnError()
    {
        var issues = AiConfigValidator.Validate([Route(AiFeature.ScriptResearch, Vertex("V", null, " "))]);

        Assert.Equal(2, issues.Count(i => i.Severity == AiConfigIssueSeverity.Error));
        Assert.Contains(issues, i => i.Message.Contains("no project id"));
        Assert.Contains(issues, i => i.Message.Contains("no location"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("claude sonnet")]
    public void ABlankOrSpacedModelId_IsAnError(string model)
    {
        var issues = AiConfigValidator.Validate([Route(AiFeature.ScriptRepair, Vertex("V", "example-project-123456", "global"), model)]);

        var error = Assert.Single(issues);
        Assert.Equal(AiConfigIssueSeverity.Error, error.Severity);
        Assert.Contains("ScriptRepair", error.Message);
    }

    [Fact]
    public void RoutesOnDifferentVertexProjects_AreOnlyAWarning()
    {
        var issues = AiConfigValidator.Validate(
        [
            Route(AiFeature.ScriptResearch, Vertex("A", "example-project-123456", "global")),
            Route(AiFeature.ScriptRepair, Vertex("B", "another-project-123456", "global")),
        ]);

        var warning = Assert.Single(issues);
        Assert.Equal(AiConfigIssueSeverity.Warning, warning.Severity);
        Assert.Contains("2 different Google Cloud projects", warning.Message);
    }

    [Fact]
    public void TheSameProjectWrittenInTwoCases_IsNotTwoProjects()
    {
        var issues = AiConfigValidator.Validate(
        [
            Route(AiFeature.ScriptResearch, Vertex("A", "example-project-123456", "global")),
            Route(AiFeature.ScriptRepair, Vertex("B", "Example-Project-123456", "us-east5")),
        ]);

        Assert.Empty(issues);
    }

    [Fact]
    public void AnApiKeyConnection_NeedsNoProjectOrLocation()
    {
        var direct = new AiConnectionSettings("Direct", AiWireProtocol.Anthropic, null, AiAuthMode.ApiKey, "k", null, null, true);

        Assert.Empty(AiConfigValidator.Validate([Route(AiFeature.ScriptResearch, direct)]));
    }

    [Fact]
    public void Summarize_JoinsOneSeveritysMessages_OrReturnsNull()
    {
        var issues = new[]
        {
            new AiConfigIssue(AiConfigIssueSeverity.Error, "one."),
            new AiConfigIssue(AiConfigIssueSeverity.Warning, "two."),
            new AiConfigIssue(AiConfigIssueSeverity.Error, "three."),
        };

        Assert.Equal("one. three.", AiConfigValidator.Summarize(issues, AiConfigIssueSeverity.Error));
        Assert.Null(AiConfigValidator.Summarize([], AiConfigIssueSeverity.Warning));
    }
}
