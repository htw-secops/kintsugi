using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Kintsugi.Application.AiSettings;
using Kintsugi.Application.Common.Exceptions;
using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Application.UpgradePaths;
using Kintsugi.Domain.Enums;
using Kintsugi.Infrastructure.Ai;

namespace Kintsugi.Tests.Infrastructure;

/// <summary>
/// Covers <see cref="AiUpgradePathResearchClient"/> against a fake <see cref="HttpMessageHandler"/>
/// (no real network calls, no real API keys) plus, for <c>CheckScriptVersionAsync</c>, a real local
/// bash subprocess (no network involved at all — it just runs a trivial script). Tests avoid
/// setting <c>ApplicationIdentifier</c> so the GitHub/GitLab hosting-site lookup (a separate,
/// best-effort HTTP round trip) never fires, keeping each test to exactly the HTTP call(s) it's
/// actually asserting about. Every fake response uses a script with no `$variable` references at
/// all, so it passes cleanly regardless of whether `shellcheck` happens to be installed in the
/// environment running these tests (the sandbox it was written in doesn't have it — the code
/// itself fails open when shellcheck can't be run, so this is deliberately not testing that path).
/// </summary>
public class AiUpgradePathResearchClientTests
{
    private const string ValidScript = "#!/bin/bash\nset -euo pipefail\n# --appName --appId --update-version --update\necho \"1.0.0\"\n";

    private static readonly AiProviderSettings AnthropicSettings = new(AiProvider.Anthropic, "sk-test-key", null, "claude-sonnet-5");

    private static UpgradePathScriptGenerationRequest Request(string? applicationIdentifier = null, string? promptOverride = null) =>
        new("Firefox", "macOS", new[] { "128.0" }, applicationIdentifier, promptOverride);

    private static HttpResponseMessage AnthropicTextResponse(string text) =>
        new(HttpStatusCode.OK) { Content = JsonContent.Create(new { content = new[] { new { type = "text", text } } }) };

    /// <summary>Dequeues one canned response per <c>SendAsync</c> call, in the order tests queue
    /// them — sufficient here since (with no ApplicationIdentifier set) each test hits exactly the
    /// AI provider endpoint, in a known, sequential order.</summary>
    private class QueueingHandler : HttpMessageHandler
    {
        private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> _responders;
        public List<HttpRequestMessage> Requests { get; } = new();

        public QueueingHandler(params Func<HttpRequestMessage, HttpResponseMessage>[] responders) =>
            _responders = new Queue<Func<HttpRequestMessage, HttpResponseMessage>>(responders);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            if (_responders.Count == 0)
            {
                throw new InvalidOperationException($"No more canned responses queued, but got another request: {request.Method} {request.RequestUri}");
            }

            return Task.FromResult(_responders.Dequeue()(request));
        }
    }

    private class ThrowingHandler : HttpMessageHandler
    {
        private readonly Exception _exception;
        public ThrowingHandler(Exception exception) => _exception = exception;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => throw _exception;
    }

    // The engine shares the handler, so the queue sees GitHub/GitLab lookups and AI calls in the
    // order they happen — exactly as when the providers were hand-written in this class.
    private static AiUpgradePathResearchClient CreateClient(HttpMessageHandler handler, IAiBudgetService? budget = null) => new(
        new HttpClient(handler, disposeHandler: false),
        TestAiEngine.Over(handler),
        FakeGitHubSettings.Provider(),
        Mock.Of<IGooseCliClient>(),
        Mock.Of<IClaudeAgentSdkClient>(),
        budget ?? Mock.Of<IAiBudgetService>(),
        Signer(),
        NullLogger<AiUpgradePathResearchClient>.Instance);

    // The one signature the fake signer accepts, standing in for this server's own over a reviewed script.
    private const string ReviewedSignature = "reviewed-signature";

    private static IArtifactSigningService Signer()
    {
        var signer = new Mock<IArtifactSigningService>();
        signer.Setup(s => s.Verify(It.IsAny<string>(), ReviewedSignature)).Returns(true);
        return signer.Object;
    }

    private static HttpResponseMessage AnthropicTextResponseWithUsage(string text, int input, int output) =>
        new(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new { content = new[] { new { type = "text", text } }, usage = new { input_tokens = input, output_tokens = output } }),
        };

    [Fact]
    public async Task GenerateScriptAsync_RecordsTheTokensItSpentAgainstTheApplication()
    {
        var budget = new Mock<IAiBudgetService>();
        IReadOnlyList<AiUsageEntry>? recorded = null;
        budget.Setup(b => b.RecordAsync(AiFeature.ScriptResearch, "Firefox", It.IsAny<IReadOnlyList<AiUsageEntry>>(), It.IsAny<CancellationToken>()))
            .Callback<AiFeature, string?, IReadOnlyList<AiUsageEntry>, CancellationToken>((_, _, entries, _) => recorded = entries)
            .Returns(Task.CompletedTask);
        var handler = new QueueingHandler(_ => AnthropicTextResponseWithUsage(ValidScript, 1200, 300));

        await CreateClient(handler, budget.Object).GenerateScriptAsync(AnthropicSettings, Request(), CancellationToken.None);

        var entry = Assert.Single(recorded!);
        Assert.Equal((1200, 300), (entry.InputTokens, entry.OutputTokens));
        budget.Verify(b => b.EnsureWithinBudgetAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GenerateScriptAsync_AtTheLimitMakesNoAiCallAtAll()
    {
        var budget = new Mock<IAiBudgetService>();
        budget.Setup(b => b.EnsureWithinBudgetAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new AiBudgetReachedException("AI budget reached: $50.00 of $50.00."));
        var handler = new QueueingHandler(_ => AnthropicTextResponse(ValidScript));

        var error = await Assert.ThrowsAsync<AiBudgetReachedException>(
            () => CreateClient(handler, budget.Object).GenerateScriptAsync(AnthropicSettings, Request(), CancellationToken.None));

        Assert.Contains("AI budget reached", error.Message);
        Assert.Empty(handler.Requests);
        budget.Verify(b => b.RecordAsync(It.IsAny<AiFeature>(), It.IsAny<string?>(), It.IsAny<IReadOnlyList<AiUsageEntry>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GenerateScriptAsync_RecordsWhatAFailedCallSpentToo()
    {
        var budget = new Mock<IAiBudgetService>();
        IReadOnlyList<AiUsageEntry>? recorded = null;
        budget.Setup(b => b.RecordAsync(It.IsAny<AiFeature>(), It.IsAny<string?>(), It.IsAny<IReadOnlyList<AiUsageEntry>>(), It.IsAny<CancellationToken>()))
            .Callback<AiFeature, string?, IReadOnlyList<AiUsageEntry>, CancellationToken>((_, _, entries, _) => recorded = entries)
            .Returns(Task.CompletedTask);
        // The model answers (tokens spent) with something that is not a script.
        var handler = new QueueingHandler(_ => AnthropicTextResponseWithUsage("", 500, 10));

        await Assert.ThrowsAnyAsync<Exception>(
            () => CreateClient(handler, budget.Object).GenerateScriptAsync(AnthropicSettings, Request(), CancellationToken.None));

        Assert.NotNull(recorded);
        Assert.Equal(500, Assert.Single(recorded!).InputTokens);
    }

    [Fact]
    public async Task GenerateScriptAsync_WithAValidScriptOnTheFirstAttempt_ReturnsFoundWithoutAnyRetry()
    {
        var handler = new QueueingHandler(_ => AnthropicTextResponse(ValidScript));
        var client = CreateClient(handler);

        var result = await client.GenerateScriptAsync(AnthropicSettings, Request(), CancellationToken.None);

        Assert.Equal(UpgradePathStatus.Found, result.Status);
        Assert.Equal(ValidScript.Trim(), result.Script);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task GenerateScriptAsync_WhenTheModelReportsNoReliableMethod_ReturnsNotFound()
    {
        var handler = new QueueingHandler(_ => AnthropicTextResponse("NO_RELIABLE_METHOD"));
        var client = CreateClient(handler);

        var result = await client.GenerateScriptAsync(AnthropicSettings, Request(), CancellationToken.None);

        Assert.Equal(UpgradePathStatus.NotFound, result.Status);
        Assert.Null(result.Script);
    }

    [Fact]
    public async Task GenerateScriptAsync_WhenTheSentinelCarriesAReason_ReturnsNotFoundQuotingIt()
    {
        var handler = new QueueingHandler(_ => AnthropicTextResponse("NO_RELIABLE_METHOD: the application is distributed only inside one organisation and publishes no release catalog."));
        var client = CreateClient(handler);

        var result = await client.GenerateScriptAsync(AnthropicSettings, Request(), CancellationToken.None);

        Assert.Equal(UpgradePathStatus.NotFound, result.Status);
        Assert.Null(result.Script);
        Assert.Contains("publishes no release catalog", result.Notes);
        Assert.DoesNotContain("NO_RELIABLE_METHOD", result.Notes);
    }

    /// <summary>The failure this guards: a model that explains instead of writing a script (or
    /// using the sentinel) used to have its explanation shellchecked, then handed back to itself as
    /// a buggy script, so the operator saw "SC1011: This apostrophe terminated the single quoted
    /// string" and never the reason. The explanation is the answer; it goes in the note, and no
    /// second call is made.</summary>
    [Fact]
    public async Task GenerateScriptAsync_WhenTheModelExplainsInsteadOfWritingAScript_ReturnsNotFoundQuotingTheExplanation()
    {
        const string prose = "I'm not able to write this script. NightMail appears to be an internal application with\nno public download location or release catalog, so there is nothing for --update-version to query.";
        var handler = new QueueingHandler(_ => AnthropicTextResponse(prose));
        var client = CreateClient(handler);

        var result = await client.GenerateScriptAsync(AnthropicSettings, Request(), CancellationToken.None);

        Assert.Equal(UpgradePathStatus.NotFound, result.Status);
        Assert.Null(result.Script);
        Assert.Contains("internal application with no public download location", result.Notes);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task GenerateScriptAsync_WhenTheFixAttemptIsAnExplanation_ThrowsQuotingIt()
    {
        const string missingContractTokens = "#!/bin/bash\nset -euo pipefail\necho \"1.0.0\"\n";
        var handler = new QueueingHandler(
            _ => AnthropicTextResponse(missingContractTokens),
            _ => AnthropicTextResponse("I'm not going to produce this script. The findings describe a contract the application cannot meet."));
        var client = CreateClient(handler);

        var ex = await Assert.ThrowsAsync<ExternalServiceException>(() => client.GenerateScriptAsync(AnthropicSettings, Request(), CancellationToken.None));
        Assert.Contains("declined to correct", ex.Message);
        Assert.Contains("contract the application cannot meet", ex.Message);
        Assert.DoesNotContain("shellcheck", ex.Message);
    }

    [Fact]
    public async Task GenerateScriptAsync_TakesTheFencedBlock_WhenTheModelWroteProseAroundIt()
    {
        var wrapped = $"Here is the script you asked for:\n\n```bash\n{ValidScript}```\n\nLet me know if you need anything else.";
        var handler = new QueueingHandler(_ => AnthropicTextResponse(wrapped));
        var client = CreateClient(handler);

        var result = await client.GenerateScriptAsync(AnthropicSettings, Request(), CancellationToken.None);

        Assert.Equal(UpgradePathStatus.Found, result.Status);
        Assert.Equal(ValidScript.Trim(), result.Script);
    }

    /// <summary>GitHub's repository search matched nothing for the bundle ID
    /// <c>au.com.sharpblue.nightmail</c> and found <c>hobleyd/nightmail</c> — releases and all — for
    /// the name, so the model got no candidates and declined to write the script. The name is
    /// searched when the identifier finds nothing, and what it finds reaches the prompt.</summary>
    [Fact]
    public async Task GenerateScriptAsync_WhenTheIdentifierMatchesNoRepository_SearchesTheNameAndHandsTheModelWhatItFound()
    {
        static HttpResponseMessage GitHubResults(params object[] items) =>
            new(HttpStatusCode.OK) { Content = JsonContent.Create(new { items }) };
        static HttpResponseMessage GitLabResults() =>
            new(HttpStatusCode.OK) { Content = JsonContent.Create(Array.Empty<object>()) };

        // Captured in the responder: the client disposes each request once it has been sent, so the
        // body is unreadable by the time the test gets to handler.Requests.
        string? prompt = null;
        var handler = new QueueingHandler(
            _ => GitHubResults(),                                                       // identifier
            _ => GitHubResults(new { full_name = "hobleyd/nightmail", html_url = "https://github.com/hobleyd/nightmail", description = "An email client", stargazers_count = 0 }), // name
            _ => GitLabResults(),                                                       // identifier
            _ => GitLabResults(),                                                       // name
            request =>
            {
                prompt = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                return AnthropicTextResponse(ValidScript);
            });
        var client = CreateClient(handler);

        var result = await client.GenerateScriptAsync(
            AnthropicSettings,
            new UpgradePathScriptGenerationRequest("NightMail", "macOS", Array.Empty<string>(), "au.com.sharpblue.nightmail"),
            CancellationToken.None);

        Assert.Equal(UpgradePathStatus.Found, result.Status);

        var searches = handler.Requests.Where(r => r.RequestUri!.Host is "api.github.com" or "gitlab.com").Select(r => Uri.UnescapeDataString(r.RequestUri!.Query)).ToList();
        Assert.Equal(4, searches.Count);
        Assert.Contains("au.com.sharpblue.nightmail", searches[0]);
        Assert.Contains("NightMail", searches[1]);

        Assert.Contains("hobleyd/nightmail", prompt);
    }

    [Fact]
    public async Task GenerateScriptAsync_StripsAMarkdownCodeFence_ThatTheModelWrappedTheScriptIn()
    {
        var fenced = $"```bash\n{ValidScript}```";
        var handler = new QueueingHandler(_ => AnthropicTextResponse(fenced));
        var client = CreateClient(handler);

        var result = await client.GenerateScriptAsync(AnthropicSettings, Request(), CancellationToken.None);

        Assert.Equal(UpgradePathStatus.Found, result.Status);
        Assert.DoesNotContain("```", result.Script);
    }

    [Fact]
    public async Task GenerateScriptAsync_WhenTheFirstAttemptFailsStructuralValidation_RetriesOnceAndSucceeds()
    {
        const string missingContractTokens = "#!/bin/bash\nset -euo pipefail\necho \"1.0.0\"\n"; // no --appName/--appId/etc anywhere
        var handler = new QueueingHandler(
            _ => AnthropicTextResponse(missingContractTokens),
            _ => AnthropicTextResponse(ValidScript));
        var client = CreateClient(handler);

        var result = await client.GenerateScriptAsync(AnthropicSettings, Request(), CancellationToken.None);

        Assert.Equal(UpgradePathStatus.Found, result.Status);
        Assert.Equal(ValidScript.Trim(), result.Script);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task GenerateScriptAsync_WhenBothTheOriginalAndTheFixAttemptFailValidation_Throws()
    {
        const string missingContractTokens = "#!/bin/bash\nset -euo pipefail\necho \"1.0.0\"\n";
        var handler = new QueueingHandler(
            _ => AnthropicTextResponse(missingContractTokens),
            _ => AnthropicTextResponse(missingContractTokens));
        var client = CreateClient(handler);

        var ex = await Assert.ThrowsAsync<ExternalServiceException>(() => client.GenerateScriptAsync(AnthropicSettings, Request(), CancellationToken.None));
        Assert.Contains("self-correction", ex.Message);
    }

    // A script that downloads something, runs it and never checks it cannot be signed on this server, so
    // it counts as a failed validation: the one repair pass is told what to add.
    private const string UncheckedDownloadScript =
        "#!/bin/bash\nset -euo pipefail\n# --appName --appId --update-version --update\n" +
        "curl -fsSL -o /tmp/app.pkg https://example.com/app.pkg\ninstaller -pkg /tmp/app.pkg -target /\n";

    private static readonly string CheckedDownloadScript =
        UncheckedDownloadScript.Replace("installer -pkg", "pkgutil --check-signature /tmp/app.pkg\ninstaller -pkg");

    [Fact]
    public async Task GenerateScriptAsync_WhenTheScriptRunsAnUncheckedDownload_TheRepairPassIsToldWhatToAdd()
    {
        var bodies = new List<string>();
        var handler = new QueueingHandler(
            r => { bodies.Add(r.Content!.ReadAsStringAsync().Result); return AnthropicTextResponse(UncheckedDownloadScript); },
            r => { bodies.Add(r.Content!.ReadAsStringAsync().Result); return AnthropicTextResponse(CheckedDownloadScript); });
        var client = CreateClient(handler);

        var result = await client.GenerateScriptAsync(AnthropicSettings, Request(), CancellationToken.None);

        Assert.Equal(UpgradePathStatus.Found, result.Status);
        Assert.Equal(CheckedDownloadScript.Trim(), result.Script);
        Assert.Null(result.Notes);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Contains("Download check", bodies[1]);
        Assert.Contains("runs it without checking it", bodies[1]);
        Assert.Contains("SHA-256", bodies[1]);
        Assert.Contains("never be computed from the downloaded file itself", bodies[1]);
        Assert.Contains("will not be signed", bodies[1]);
    }

    [Fact]
    public async Task GenerateScriptAsync_WhenTheRepairStillRunsAnUncheckedDownload_KeepsTheScriptAndExplainsInItsNotes()
    {
        var handler = new QueueingHandler(
            _ => AnthropicTextResponse(UncheckedDownloadScript),
            _ => AnthropicTextResponse(UncheckedDownloadScript));
        var client = CreateClient(handler);

        var result = await client.GenerateScriptAsync(AnthropicSettings, Request(), CancellationToken.None);

        // Kept (it will be unsigned, like every generated script) with the reason, instead of thrown.
        Assert.Equal(UpgradePathStatus.Found, result.Status);
        Assert.Equal(UncheckedDownloadScript.Trim(), result.Script);
        Assert.Contains("runs it without checking it", result.Notes);
        Assert.Contains("https://example.com/app.pkg", result.Notes);
    }

    // The original passed every analyzer; only the download check failed. A repair that goes wrong must not cost
    // the original: it is kept, with the finding in its notes, as it was before the download check existed.
    [Fact]
    public async Task GenerateScriptAsync_WhenTheRepairAnswersInProse_KeepsTheOriginalValidScript()
    {
        var handler = new QueueingHandler(
            _ => AnthropicTextResponse(UncheckedDownloadScript),
            _ => AnthropicTextResponse("I'm not going to change this script. The download comes from the vendor's own site and I cannot publish a checksum for it."));
        var client = CreateClient(handler);

        var result = await client.GenerateScriptAsync(AnthropicSettings, Request(), CancellationToken.None);

        Assert.Equal(UpgradePathStatus.Found, result.Status);
        Assert.Equal(UncheckedDownloadScript.Trim(), result.Script);
        Assert.Contains("runs it without checking it", result.Notes);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task GenerateScriptAsync_WhenTheRepairFailsValidation_KeepsTheOriginalValidScript()
    {
        const string missingContractTokens = "#!/bin/bash\nset -euo pipefail\ncurl -fsSL -o /tmp/app.pkg https://example.com/app.pkg\ninstaller -pkg /tmp/app.pkg -target /\n";
        var handler = new QueueingHandler(
            _ => AnthropicTextResponse(UncheckedDownloadScript),
            _ => AnthropicTextResponse(missingContractTokens));
        var client = CreateClient(handler);

        var result = await client.GenerateScriptAsync(AnthropicSettings, Request(), CancellationToken.None);

        Assert.Equal(UpgradePathStatus.Found, result.Status);
        Assert.Equal(UncheckedDownloadScript.Trim(), result.Script);
        Assert.Contains("https://example.com/app.pkg", result.Notes);
    }

    [Fact]
    public async Task GenerateScriptAsync_TheRepairPromptNamesTheToolsOfThePlatformOfTheScript()
    {
        var bodies = new List<string>();
        var handler = new QueueingHandler(
            r => { bodies.Add(r.Content!.ReadAsStringAsync().Result); return AnthropicTextResponse(UncheckedDownloadScript); },
            r => { bodies.Add(r.Content!.ReadAsStringAsync().Result); return AnthropicTextResponse(CheckedDownloadScript); });

        await CreateClient(handler).GenerateScriptAsync(AnthropicSettings, Request(), CancellationToken.None);

        // Request() is a macOS script: it is told about pkgutil and codesign, not PowerShell's cmdlets.
        Assert.Contains("pkgutil --check-signature", bodies[1]);
        Assert.DoesNotContain("Get-AuthenticodeSignature", bodies[1].Split("Download check", 2)[1]);
    }

    [Fact]
    public async Task GenerateScriptAsync_WhenTheScriptChecksItsDownload_NeedsNoRepair()
    {
        var handler = new QueueingHandler(_ => AnthropicTextResponse(CheckedDownloadScript));
        var client = CreateClient(handler);

        var result = await client.GenerateScriptAsync(AnthropicSettings, Request(), CancellationToken.None);

        Assert.Equal(CheckedDownloadScript.Trim(), result.Script);
        Assert.Null(result.Notes);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task GenerateScriptAsync_WithAnEmptyModelResponse_ThrowsRatherThanPersistingAnUnusableScript()
    {
        var handler = new QueueingHandler(_ => AnthropicTextResponse(""));
        var client = CreateClient(handler);

        await Assert.ThrowsAsync<ExternalServiceException>(() => client.GenerateScriptAsync(AnthropicSettings, Request(), CancellationToken.None));
    }

    [Fact]
    public async Task GenerateScriptAsync_WhenTheProviderReturnsAnErrorStatus_ThrowsExternalServiceException()
    {
        var handler = new QueueingHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("invalid api key") });
        var client = CreateClient(handler);

        // A subclass: a refused credential is reported as "AI provider misconfigured: …".
        var ex = await Assert.ThrowsAnyAsync<ExternalServiceException>(() => client.GenerateScriptAsync(AnthropicSettings, Request(), CancellationToken.None));
        Assert.Contains("401", ex.Message);
        Assert.StartsWith("AI provider misconfigured: ", ex.Message);
    }

    [Fact]
    public async Task GenerateScriptAsync_WhenTheProviderIsUnreachable_ThrowsExternalServiceException_NotARawNetworkException()
    {
        var client = CreateClient(new ThrowingHandler(new HttpRequestException("connection refused")));

        await Assert.ThrowsAsync<ExternalServiceException>(() => client.GenerateScriptAsync(AnthropicSettings, Request(), CancellationToken.None));
    }

    [Fact]
    public void BuildDefaultPrompt_IncludesTheApplicationNamePlatformAndKnownVersions()
    {
        var client = CreateClient(new QueueingHandler());

        var prompt = client.BuildDefaultPrompt(new UpgradePathScriptGenerationRequest("Firefox", "macOS", new[] { "128.0", "127.0" }));

        Assert.Contains("Firefox", prompt);
        Assert.Contains("macOS", prompt);
        Assert.Contains("128.0, 127.0", prompt);
    }

    [Fact]
    public void BuildDefaultPrompt_WithNoKnownInstalledVersions_SaysUnknownRatherThanAnEmptyList()
    {
        var client = CreateClient(new QueueingHandler());

        var prompt = client.BuildDefaultPrompt(new UpgradePathScriptGenerationRequest("Firefox", "macOS", Array.Empty<string>()));

        Assert.Contains("unknown", prompt);
    }

    // What an administrator can read when a patch fails depends on these three rules, and the model
    // ignores any it is not told: it hides installer output on its own and, left alone, reports a
    // failed signature check next to an install that went ahead.
    [Theory]
    [InlineData(PlatformBucket.Windows, "Authenticode signature OK")]
    [InlineData(PlatformBucket.MacOs, "Team ID OK")]
    [InlineData(PlatformBucket.Linux, "signature OK")]
    public void BuildDefaultPrompt_TellsTheModelNotToHideInstallerOutput_ToLogEachCheck_AndToVerifyBeforeInstalling(
        string platform, string platformCheck)
    {
        var client = CreateClient(new QueueingHandler());

        var prompt = client.BuildDefaultPrompt(new UpgradePathScriptGenerationRequest("Firefox", platform, Array.Empty<string>()));

        Assert.Contains("Never hide", prompt);
        Assert.Contains("Send it to stderr", prompt);
        Assert.Contains("`checksum OK`", prompt);
        Assert.Contains(platformCheck, prompt);
        Assert.Contains("never print an OK for a check", prompt);
        Assert.Contains("BEFORE the installer or package manager is run", prompt);
        Assert.Contains("exit non-zero", prompt);
    }

    // An installer an unattended script downloads is run as root or SYSTEM, so the script must check it
    // first. The validator does not enforce this (a human reviews the script); the prompt asks for it.
    [Theory]
    [InlineData(PlatformBucket.Windows, "Get-FileHash -Algorithm SHA256", "Get-AuthenticodeSignature")]
    [InlineData(PlatformBucket.MacOs, "shasum -a 256", "pkgutil --check-signature")]
    [InlineData(PlatformBucket.Linux, "sha256sum", "gpg --verify")]
    public void BuildDefaultPrompt_TellsTheModelToVerifyAnInstallerItDownloads(string platform, string hashTool, string signatureTool)
    {
        var client = CreateClient(new QueueingHandler());

        var prompt = client.BuildDefaultPrompt(new UpgradePathScriptGenerationRequest("Firefox", platform, Array.Empty<string>()));

        Assert.Contains("verify the download before running it", prompt);
        Assert.Contains("publishes a SHA-256", prompt);
        Assert.Contains(hashTool, prompt);
        Assert.Contains(signatureTool, prompt);
        Assert.Contains("Never run a download", prompt);

        // The sign gate refuses an unchecked download however it is commented, and a file always matches a hash
        // taken from itself (#68's review): the prompt must not invite either.
        Assert.Contains("will not be signed", prompt);
        Assert.Contains("never be computed from the file the script just downloaded", prompt);
        Assert.DoesNotContain("the one case to say so", prompt);
        Assert.DoesNotContain("say so in a comment rather than silently trusting", prompt);
    }

    [Fact]
    public void BuildDefaultPrompt_ForMacOs_NoLongerSuggestsSilencingHdiutil()
    {
        var client = CreateClient(new QueueingHandler());

        var prompt = client.BuildDefaultPrompt(new UpgradePathScriptGenerationRequest("Firefox", PlatformBucket.MacOs, Array.Empty<string>()));

        // `-quiet` is still named once, in the rule that forbids it.
        Assert.DoesNotContain("hdiutil attach -nobrowse -quiet", prompt);
        Assert.Contains("hdiutil attach -nobrowse`", prompt);
    }

    [Fact]
    public void BuildDefaultPrompt_ForWindows_AsksForAPowerShellScript_ThatStillRunsItsVersionCheckOnLinux()
    {
        var client = CreateClient(new QueueingHandler());

        var prompt = client.BuildDefaultPrompt(new UpgradePathScriptGenerationRequest("Firefox", PlatformBucket.Windows, Array.Empty<string>()));

        Assert.Contains("PowerShell script", prompt);
        Assert.Contains("script.ps1 --appName", prompt);
        // The constraint that makes server-side version checking possible at all: --update-version
        // has to run under pwsh on this Linux server, so it can't reach for the registry or WMI.
        Assert.Contains("plain Linux server under PowerShell", prompt);
        Assert.Contains("no registry access", prompt);
        Assert.DoesNotContain("#!/bin/bash", prompt);
    }

    [Fact]
    public void BuildDefaultPrompt_ForMacOs_StillAsksForBash()
    {
        var client = CreateClient(new QueueingHandler());

        var prompt = client.BuildDefaultPrompt(new UpgradePathScriptGenerationRequest("Firefox", PlatformBucket.MacOs, Array.Empty<string>()));

        Assert.Contains("bash script", prompt);
        Assert.Contains("`#!/bin/bash`", prompt);
        Assert.DoesNotContain("PowerShell", prompt);
    }

    [Fact]
    public void BuildDefaultPrompt_ForWindows_DescribesTheIdentifierAsARegistryKey_NotABundleId()
    {
        var client = CreateClient(new QueueingHandler());

        var prompt = client.BuildDefaultPrompt(
            new UpgradePathScriptGenerationRequest("Firefox", PlatformBucket.Windows, Array.Empty<string>(), "Mozilla Firefox"));

        Assert.Contains("Mozilla Firefox", prompt);
        Assert.Contains("uninstall registry", prompt);
        Assert.DoesNotContain("macOS bundle ID", prompt);
    }

    [Fact]
    public void BuildDefaultPrompt_ForLinux_AsksForBash_AndForbidsAskingTheApiServersOwnPackageManager()
    {
        var client = CreateClient(new QueueingHandler());

        var prompt = client.BuildDefaultPrompt(new UpgradePathScriptGenerationRequest("Firefox", PlatformBucket.Linux, Array.Empty<string>()));

        Assert.Contains("a Linux application", prompt);
        Assert.Contains("bash script", prompt);
        Assert.DoesNotContain("PowerShell", prompt);

        // The trap that only exists on this platform. --update-version runs on the API server,
        // which is itself a Linux machine, so `apt-cache policy` there does not fail — it answers
        // confidently about the wrong host, and that answer becomes LatestVersion for the whole
        // fleet. macOS and Windows get this for free because their host-local tools simply aren't
        // present on the server.
        Assert.Contains("runs directly on the fleet-management API server", prompt);
        Assert.Contains("apt-cache", prompt);
        Assert.Contains("Never use any of them", prompt);
    }

    [Fact]
    public void BuildDefaultPrompt_ForLinux_TellsTheUpdateModeToDetectThePackageManagerRatherThanAssumeApt()
    {
        var client = CreateClient(new QueueingHandler());

        var prompt = client.BuildDefaultPrompt(new UpgradePathScriptGenerationRequest("Firefox", PlatformBucket.Linux, Array.Empty<string>()));

        Assert.Contains("Do NOT assume a distribution", prompt);
        Assert.Contains("zypper", prompt);
        Assert.Contains("DEBIAN_FRONTEND=noninteractive", prompt);
    }

    [Fact]
    public void BuildDefaultPrompt_ForLinux_DescribesTheIdentifierAsTheReportedOne_NotAMacBundleId()
    {
        var client = CreateClient(new QueueingHandler());

        var prompt = client.BuildDefaultPrompt(
            new UpgradePathScriptGenerationRequest("Firefox", PlatformBucket.Linux, Array.Empty<string>(), "org.mozilla.firefox"));

        Assert.Contains("org.mozilla.firefox", prompt);
        Assert.Contains("Flatpak application ID", prompt);
        Assert.DoesNotContain("macOS bundle ID", prompt);
    }

    /// <summary>
    /// An unrecognizable operating system string lands in <see cref="PlatformBucket.Generic"/>, and
    /// that bucket has always been prompted for as macOS. Adding a third platform must not have
    /// quietly changed which one it falls back to.
    /// </summary>
    [Fact]
    public void BuildDefaultPrompt_ForTheGenericBucket_StillPromptsAsMacOs()
    {
        var client = CreateClient(new QueueingHandler());

        var prompt = client.BuildDefaultPrompt(new UpgradePathScriptGenerationRequest("Firefox", PlatformBucket.Generic, Array.Empty<string>()));

        Assert.Contains("a macOS application", prompt);
        Assert.Contains("`#!/bin/bash`", prompt);
    }

    [Fact]
    public void BuildDefaultPrompt_IncludesTheApplicationIdentifier_WhenProvided()
    {
        var client = CreateClient(new QueueingHandler());

        var prompt = client.BuildDefaultPrompt(new UpgradePathScriptGenerationRequest("Firefox", "macOS", Array.Empty<string>(), "org.mozilla.firefox"));

        Assert.Contains("org.mozilla.firefox", prompt);
    }

    [Fact]
    public async Task CheckScriptVersionAsync_WithAWellBehavedScript_ReturnsTheBareVersionString()
    {
        var client = CreateClient(new QueueingHandler());
        const string script = "#!/bin/bash\necho \"129.0\"\nexit 0\n";

        var result = await client.CheckScriptVersionAsync(script, ReviewedSignature, PlatformBucket.MacOs, "Firefox", "org.mozilla.firefox", CancellationToken.None);

        Assert.Equal("129.0", result);
    }

    [Fact]
    public async Task CheckScriptVersionAsync_WithANonZeroExit_ReturnsNull()
    {
        var client = CreateClient(new QueueingHandler());
        const string script = "#!/bin/bash\necho \"broken\" >&2\nexit 1\n";

        var result = await client.CheckScriptVersionAsync(script, ReviewedSignature, PlatformBucket.MacOs, "Firefox", "org.mozilla.firefox", CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task CheckScriptVersionAsync_WithMultiLineStdout_ReturnsNull_RatherThanTheFirstLine()
    {
        var client = CreateClient(new QueueingHandler());
        const string script = "#!/bin/bash\necho \"129.0\"\necho \"unexpected extra line\"\n";

        var result = await client.CheckScriptVersionAsync(script, ReviewedSignature, PlatformBucket.MacOs, "Firefox", "org.mozilla.firefox", CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task CheckScriptVersionAsync_WithBlankStdout_ReturnsNull()
    {
        var client = CreateClient(new QueueingHandler());
        const string script = "#!/bin/bash\nexit 0\n";

        var result = await client.CheckScriptVersionAsync(script, ReviewedSignature, PlatformBucket.MacOs, "Firefox", "org.mozilla.firefox", CancellationToken.None);

        Assert.Null(result);
    }

    // htw-secops/kintsugi#8: the server runs a script only if it signed those exact bytes. Each script
    // below would leave a marker file behind if it ran at all, so "null" cannot hide a run.
    [Theory]
    [InlineData(null)]
    [InlineData("a-signature-this-server-never-made")]
    public async Task CheckScriptVersionAsync_WithAScriptThisServerDidNotSign_NeverRunsIt(string? signature)
    {
        var client = CreateClient(new QueueingHandler());
        var marker = Path.Combine(Path.GetTempPath(), $"kintsugi-ran-{Guid.NewGuid():N}");
        var script = $"#!/bin/bash\ntouch '{marker}'\necho \"129.0\"\n";

        var result = await client.CheckScriptVersionAsync(script, signature, PlatformBucket.MacOs, "Firefox", "org.mozilla.firefox", CancellationToken.None);

        Assert.Null(result);
        Assert.False(File.Exists(marker));
    }
}
