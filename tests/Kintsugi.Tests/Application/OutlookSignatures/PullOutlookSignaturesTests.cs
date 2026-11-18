using Kintsugi.Application.Common.Exceptions;
using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Application.OutlookSignatures;
using Kintsugi.Application.OutlookSignatures.Commands.PullOutlookSignatures;
using Kintsugi.Domain.Entities;
using Kintsugi.Infrastructure.Persistence;
using Kintsugi.Infrastructure.Persistence.Repositories;
using static Kintsugi.Tests.Application.Baselines.BaselineTestData;

namespace Kintsugi.Tests.Application.OutlookSignatures;

/// <summary>Each pull outcome over the real repositories: ok replaces the set, disabled empties it, and
/// anything else — an error, a 401, a malformed body, one entry over a cap — changes nothing.</summary>
public class PullOutlookSignaturesTests : IAsyncLifetime
{
    private readonly ApplicationDbContext _context = Context();
    private readonly FakeSource _source = new();
    private OutlookSignatureSourceSettingsRepository _settings = default!;
    private PersonSignatureRepository _signatures = default!;

    public async Task InitializeAsync()
    {
        _settings = new OutlookSignatureSourceSettingsRepository(_context);
        _signatures = new PersonSignatureRepository(_context);
        await _settings.AddAsync(
            OutlookSignatureSourceSettings.Create(true, "https://signatures.example.com/bulk", "https://signatures.example.com", ""),
            CancellationToken.None);
        await _context.SaveChangesAsync();
    }

    public Task DisposeAsync() => _context.DisposeAsync().AsTask();

    private Task<OutlookSignaturePullResult?> Pull() =>
        new PullOutlookSignaturesCommandHandler(_settings, _signatures, _source, _context)
            .Handle(new PullOutlookSignaturesCommand(), CancellationToken.None);

    private static SourceSignature Entry(string email, string html = "<p>Example (managed)</p>") =>
        new(email, html, "Example (managed)", "2026-11-18T01:00:00Z");

    private static OutlookSignatureSourceResponse Ok(params SourceSignature?[] entries) => new("ok", entries);

    private async Task<Dictionary<string, PersonSignature>> Held() =>
        (await _signatures.GetAllAsync(CancellationToken.None)).ToDictionary(s => s.Email);

    private async Task SeedTwo()
    {
        _source.Next = () => Ok(Entry("ana@example.com"), Entry("ben@example.com"));
        await Pull();
    }

    [Fact]
    public async Task Ok_UpsertsEveryone_DeletesTheAbsent_AndHashesTheHtmlItself()
    {
        await SeedTwo();
        var before = (await Held())["ana@example.com"].Id;

        _source.Next = () => Ok(Entry("ANA@Example.com", "<p>new</p>"), Entry("cy@example.com"));
        var result = await Pull();

        Assert.Equal(new OutlookSignaturePullResult("ok", 2, null), result);
        var held = await Held();
        Assert.Equal(new[] { "ana@example.com", "cy@example.com" }, held.Keys.Order());
        Assert.Equal(before, held["ana@example.com"].Id);
        Assert.Equal("<p>new</p>", held["ana@example.com"].Html);
        Assert.Equal(PersonSignature.HashOf("<p>new</p>"), held["ana@example.com"].Sha256);
        Assert.Equal(new DateTimeOffset(2026, 11, 18, 1, 0, 0, TimeSpan.Zero), held["ana@example.com"].RenderedAtUtc);

        var settings = (await _settings.GetAsync(CancellationToken.None))!;
        Assert.Equal("ok", settings.LastPullStatus);
        Assert.Equal(2, settings.LastPullCount);
        Assert.Null(settings.LastError);
    }

    [Fact]
    public async Task Disabled_DeletesEveryCopy()
    {
        await SeedTwo();
        _source.Next = () => new OutlookSignatureSourceResponse("disabled", null);

        var result = await Pull();

        Assert.Equal(new OutlookSignaturePullResult("disabled", 0, null), result);
        Assert.Empty(await Held());
        Assert.Equal(0, (await _settings.GetAsync(CancellationToken.None))!.LastPullCount);
    }

    /// <summary>A 401, a timeout and an unreachable host all reach the handler as the client's exception.</summary>
    [Fact]
    public async Task AnErrorOr401_KeepsTheCopies_AndRecordsWhy()
    {
        await SeedTwo();
        _source.Next = () => throw new ExternalServiceException("The signature source refused this server's identity (HTTP 401).");

        var result = await Pull();

        Assert.Equal("error", result!.Status);
        Assert.Equal(2, result.Count);
        Assert.Equal(2, (await Held()).Count);
        var settings = (await _settings.GetAsync(CancellationToken.None))!;
        Assert.Equal("error", settings.LastPullStatus);
        Assert.Contains("401", settings.LastError);
        Assert.Equal(2, settings.LastPullCount);
    }

    public static IEnumerable<object[]> Refused()
    {
        var big = new string('x', PersonSignature.MaxBodyBytes + 1);
        // Two bytes a character in UTF-8, so this is over the cap in bytes while under it in characters.
        var wide = new string('é', PersonSignature.MaxBodyBytes / 2 + 1);
        yield return new object[] { "too many", Ok(Enumerable.Range(0, OutlookSignatureRules.MaxEntries + 1).Select(i => (SourceSignature?)Entry($"p{i}@example.com")).ToArray()) };
        yield return new object[] { "html over cap", Ok(Entry("ana@example.com"), Entry("ben@example.com", big)) };
        yield return new object[] { "html over cap in bytes", Ok(Entry("ana@example.com", wide)) };
        yield return new object[] { "text over cap", Ok(Entry("ana@example.com") with { Text = big }) };
        yield return new object[] { "bad email", Ok(Entry("ana@example.com"), Entry("Ana Example")) };
        yield return new object[] { "blank email", Ok(Entry("ana@example.com"), Entry("  ")) };
        yield return new object[] { "duplicate email", Ok(Entry("ana@example.com"), Entry("Ana@Example.com")) };
        yield return new object[] { "null entry", Ok(Entry("ana@example.com"), null) };
        yield return new object[] { "missing html", Ok(Entry("ana@example.com") with { Html = null }) };
        yield return new object[] { "bad renderedAt", Ok(Entry("ana@example.com") with { RenderedAt = "yesterday" }) };
        yield return new object[] { "ok without a list", new OutlookSignatureSourceResponse("ok", null) };
        yield return new object[] { "unknown status", new OutlookSignatureSourceResponse("paused", null) };
    }

    /// <summary>Over any cap, or malformed anywhere: the whole response is refused and nothing is half applied.</summary>
    [Theory]
    [MemberData(nameof(Refused))]
    public async Task AnOversizeOrMalformedResponse_IsRefusedWhole(string why, OutlookSignatureSourceResponse response)
    {
        await SeedTwo();
        var before = await Held();
        _source.Next = () => response;

        var result = await Pull();

        Assert.True(result!.Status == "error", why);
        var after = await Held();
        Assert.Equal(before.Keys.Order(), after.Keys.Order());
        Assert.All(after.Values, s => Assert.Equal("<p>Example (managed)</p>", s.Html));
        var error = (await _settings.GetAsync(CancellationToken.None))!.LastError!;
        Assert.Contains("Nothing was changed", error);
        Assert.DoesNotContain("@", error);
    }

    [Fact]
    public async Task NothingIsFetched_WhenThePullIsSwitchedOff()
    {
        var settings = (await _settings.GetAsync(CancellationToken.None))!;
        settings.Update(false, settings.EndpointUrl, settings.Audience, "");
        await _context.SaveChangesAsync();
        _source.Next = () => throw new InvalidOperationException("must not be called");

        Assert.Null(await Pull());
        Assert.Equal(0, _source.Calls);
    }

    private sealed class FakeSource : IOutlookSignatureSourceClient
    {
        public Func<OutlookSignatureSourceResponse> Next { get; set; } = () => new OutlookSignatureSourceResponse("ok", Array.Empty<SourceSignature>());

        public int Calls { get; private set; }

        public Task<OutlookSignatureSourceResponse> FetchAsync(string endpointUrl, string audience, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(Next());
        }
    }
}
