using Kintsugi.Application.Baselines;
using Kintsugi.Application.Baselines.Commands.AssignBaseline;
using Kintsugi.Application.Baselines.Commands.CreateBaseline;
using Kintsugi.Application.Baselines.Commands.ReportBaselineResults;
using Kintsugi.Application.Baselines.Commands.ResolveBaselinePeople;
using Kintsugi.Application.Baselines.Commands.SignBaselineVersion;
using Kintsugi.Application.Baselines.Queries.GetAgentBaselines;
using Kintsugi.Application.Baselines.Queries.GetBaseline;
using Kintsugi.Application.Baselines.Queries.GetBaselineHost;
using Kintsugi.Application.Baselines.Queries.GetBaselinePeople;
using Kintsugi.Application.Baselines.Queries.GetBaselines;
using Kintsugi.Application.Baselines.Queries.GetInstalledApplicationNames;
using Kintsugi.Domain.Entities;
using Kintsugi.Domain.Enums;
using Kintsugi.Infrastructure.Persistence;
using Kintsugi.Infrastructure.Persistence.Repositories;
using static Kintsugi.Tests.Application.Baselines.BaselineTestData;

namespace Kintsugi.Tests.Application.Baselines;

/// <summary>
/// Contract items 3–8 of IP-874 end to end over the real repositories: the people route, how a person is
/// resolved, per-person results, how they count, assignment by installed application, and the drill-down.
/// Every address and name is a placeholder: this repository is public.
/// </summary>
public class BaselinePeopleTests : IAsyncLifetime
{
    private const string App = "Example Mail Sync";
    private const string Ana = "ana@example.com";
    private const string Ben = "ben@example.com";

    private readonly ApplicationDbContext _context = Context();
    private BaselineRepository _baselines = default!;
    private HostRepository _hosts = default!;
    private InstalledApplicationRepository _applications = default!;
    private PersonSignatureRepository _signatures = default!;
    private OutlookSignatureSourceSettingsRepository _source = default!;
    private readonly Host _pc1 = new("pc-1", "SERIAL-1", "Windows 11 Pro");
    private readonly Host _pc2 = new("pc-2", "SERIAL-2", "Windows 11 Pro");
    private readonly Host _mac = new("mac-1", "SERIAL-MAC", "macOS 15.0");
    private Guid _baselineId;
    private Guid _versionId;

    public async Task InitializeAsync()
    {
        _baselines = new BaselineRepository(_context);
        _hosts = new HostRepository(_context);
        _applications = new InstalledApplicationRepository(_context);
        _signatures = new PersonSignatureRepository(_context);
        _source = new OutlookSignatureSourceSettingsRepository(_context);

        foreach (var host in new[] { _pc1, _pc2, _mac })
        {
            await _hosts.AddAsync(host, CancellationToken.None);
        }

        // The application on pc-1 and the Mac, spelt differently from the assignment: the match ignores case.
        await _applications.AddRangeAsync(new[]
        {
            new InstalledApplication(_pc1.Id, "example mail sync", "4.3"),
            new InstalledApplication(_mac.Id, App, "4.3"),
            new InstalledApplication(_pc2.Id, "Something Else", "1.0"),
        }, CancellationToken.None);

        await _source.AddAsync(OutlookSignatureSourceSettings.Create(false, null, null, ".ex"), CancellationToken.None);
        foreach (var email in new[] { Ana, Ben, "abcdefghijklmnopqrst1@example.com", "abcdefghijklmnopqrst2@example.com" })
        {
            await _signatures.AddAsync(PersonSignature.Create(email, $"<p>{email}</p>", email, DateTimeOffset.UtcNow.AddHours(-3), DateTimeOffset.UtcNow), CancellationToken.None);
        }

        await _context.SaveChangesAsync();

        var created = await new CreateBaselineCommandHandler(_baselines, _context)
            .Handle(new CreateBaselineCommand("Outlook signatures", new[] { OutlookSignature(), Setting("copilot.off") }), CancellationToken.None);
        await new SignBaselineVersionCommandHandler(_baselines, SigningService(), _context)
            .Handle(new SignBaselineVersionCommand(created.VersionId, created.DocumentSha256), CancellationToken.None);
        (_baselineId, _versionId) = (created.BaselineId, created.VersionId);
        await Assign(App.ToUpperInvariant());
    }

    public Task DisposeAsync() => _context.DisposeAsync().AsTask();

    private Task Assign(string? application, bool allWindows = false) =>
        new AssignBaselineCommandHandler(_baselines, _hosts, _context)
            .Handle(new AssignBaselineCommand(_baselineId, allWindows, Array.Empty<string>(), ApplicationName: application), CancellationToken.None);

    private Task<BaselinePeopleDto> People(string serial, params SessionInput[] sessions) =>
        new ResolveBaselinePeopleCommandHandler(_baselines, _hosts, _applications, _signatures, _source, _context)
            .Handle(new ResolveBaselinePeopleCommand(serial, sessions), CancellationToken.None);

    private Task Report(string serial, params ItemResultInput[] items) =>
        new ReportBaselineResultsCommandHandler(_baselines, _hosts, _applications, _context)
            .Handle(new ReportBaselineResultsCommand(serial, new[] { new BaselineResultInput(_versionId, items, Array.Empty<FactResultInput>()) }), CancellationToken.None);

    private static ItemResultInput Signature(string state, string? subject, string? detail = null) =>
        new("outlook.signature", state, null, detail, subject);

    private async Task<ComplianceCountsDto> SignatureCounts() =>
        (await new GetBaselineQueryHandler(_baselines, _hosts, _applications).Handle(new GetBaselineQuery(_baselineId), CancellationToken.None))
            .Items.Single(i => i.Item.Key == "outlook.signature").Counts;

    private async Task<IReadOnlyList<HostBaselineItemState>> Rows(Host host) =>
        await _baselines.GetHostStatesAsync(host.Id, new[] { _versionId }, CancellationToken.None);

    // --- 7. Assign by installed application -------------------------------------------------------------

    [Fact]
    public async Task AssignedByApplication_ReachesWindowsHostsWithIt_IgnoringCase_AndNoOthers()
    {
        var fetch = new GetAgentBaselinesQueryHandler(_baselines, _hosts, _applications);

        Assert.Single((await fetch.Handle(new GetAgentBaselinesQuery("SERIAL-1"), CancellationToken.None)).Baselines);
        Assert.Empty((await fetch.Handle(new GetAgentBaselinesQuery("SERIAL-2"), CancellationToken.None)).Baselines);
        // Only the Windows agent fetches baselines, so a Mac with the application is not assigned.
        Assert.Empty((await fetch.Handle(new GetAgentBaselinesQuery("SERIAL-MAC"), CancellationToken.None)).Baselines);

        var summary = Assert.Single(await new GetBaselinesQueryHandler(_baselines, _hosts, _applications).Handle(new GetBaselinesQuery(), CancellationToken.None));
        Assert.Equal(1, summary.ApplicableHostCount);
        Assert.Equal(App.ToUpperInvariant(), summary.AssignedApplicationName);
        Assert.Equal(2, summary.SettingCount);
    }

    [Fact]
    public async Task AnApplicationNameOver255Characters_IsRefused()
    {
        var validator = new AssignBaselineCommandValidator();

        Assert.False(validator.Validate(new AssignBaselineCommand(_baselineId, false, Array.Empty<string>(), ApplicationName: new string('a', 256))).IsValid);
        Assert.True(validator.Validate(new AssignBaselineCommand(_baselineId, false, Array.Empty<string>(), ApplicationName: new string('a', 255))).IsValid);
    }

    [Fact]
    public async Task InstalledApplicationNames_AreDistinct_Filtered_AndCappedAt50()
    {
        await _applications.AddRangeAsync(
            Enumerable.Range(0, 60).Select(i => new InstalledApplication(_pc2.Id, $"Example Tool {i:00}", "1.0")), CancellationToken.None);
        await _context.SaveChangesAsync();
        var names = new GetInstalledApplicationNamesQueryHandler(_applications);

        Assert.Equal(50, (await names.Handle(new GetInstalledApplicationNamesQuery("example tool"), CancellationToken.None)).Count);
        var sync = await names.Handle(new GetInstalledApplicationNamesQuery("MAIL SYNC"), CancellationToken.None);
        Assert.Equal(new[] { App, "example mail sync" }.Order(StringComparer.Ordinal), sync.Order(StringComparer.Ordinal));
    }

    // --- 3. The route's answer, and 4. resolving a person -------------------------------------------------

    [Fact]
    public async Task AHostServedNoPerPersonItem_GetsNobody_AndNothingIsRecorded()
    {
        Assert.Empty((await People("SERIAL-2", new SessionInput("ana", Ana))).People);
        Assert.Empty((await People("NOT-ENROLLED", new SessionInput("ana", Ana))).People);
        Assert.Empty(await _baselines.GetResolutionsAsync(_pc2.Id, CancellationToken.None));
    }

    [Fact]
    public async Task AGcpwEmailWithACopy_ResolvesFirst_AndServesThatCopy()
    {
        var person = Assert.Single((await People("SERIAL-1", new SessionInput("whoever", "  Ana@Example.COM "))).People);

        Assert.Equal(("whoever", Ana, "gcpw", (string?)null), (person.WindowsUser, person.Email, person.ResolvedBy, person.Reason));
        Assert.Equal($"<p>{Ana}</p>", person.Copy!.Html);
        Assert.Equal(PersonSignature.HashOf($"<p>{Ana}</p>"), person.Copy.Sha256);
        Assert.Empty(person.States);
    }

    [Fact]
    public async Task WithoutGcpw_TheUsernameIsMapped_WithTheSuffix_IgnoringCase()
    {
        var person = Assert.Single((await People("SERIAL-1", new SessionInput("BEN.EX", null))).People);

        Assert.Equal((Ben, "username"), (person.Email, person.ResolvedBy));
    }

    /// <summary>Windows cuts a username at 20 characters, so two long addresses can map to one name: then
    /// nobody is chosen, rather than the wrong person.</summary>
    [Fact]
    public async Task AUsernameMatchingSeveralPeopleOnceCut_ResolvesNobody()
    {
        Assert.Equal("abcdefghijklmnopqrst", ResolveBaselinePeopleCommandHandler.MappedUsername("abcdefghijklmnopqrst1@example.com", ".ex"));

        var person = Assert.Single((await People("SERIAL-1", new SessionInput("abcdefghijklmnopqrst", null))).People);

        Assert.Null(person.Email);
        Assert.Null(person.Copy);
        Assert.Contains("2 people", person.Reason);
    }

    [Fact]
    public async Task NoMatch_ResolvesNobody_WithTheReason_AndAGcpwAccountWithoutACopyIsSaid()
    {
        var people = (await People("SERIAL-1", new SessionInput("cy", null), new SessionInput("dee", "dee@example.com"))).People;

        Assert.All(people, p => Assert.Null(p.Email));
        Assert.Equal("no person with a signature matches this Windows username", people[0].Reason);
        Assert.StartsWith("the signed-in Google account has no signature", people[1].Reason);
    }

    [Fact]
    public async Task OnlyThisRequestsPeople_AreServedCopies()
    {
        await People("SERIAL-1", new SessionInput("ben.ex", null));

        var people = (await People("SERIAL-1", new SessionInput("x", Ana))).People;

        Assert.Equal(new[] { Ana }, people.Select(p => p.Copy?.Html is null ? null : p.Email));
    }

    [Fact]
    public async Task EveryOutcomeIsRecordedPerHost_AndRowsUnseenFor30DaysArePruned()
    {
        await Assign(App, allWindows: true);
        await People("SERIAL-2", new SessionInput("old", null));
        var old = Assert.Single(await _baselines.GetResolutionsAsync(_pc2.Id, CancellationToken.None));
        _context.Entry(old).Property(r => r.LastSeenUtc).CurrentValue = DateTimeOffset.UtcNow.AddDays(-31);
        await _context.SaveChangesAsync();

        await People("SERIAL-1", new SessionInput("ben.ex", null), new SessionInput("BEN.EX", null), new SessionInput("cy", null));

        var recorded = (await _baselines.GetResolutionsAsync(_pc1.Id, CancellationToken.None)).OrderBy(r => r.WindowsUser).ToList();
        Assert.Equal(new[] { ("ben.ex", Ben), ("cy", (string?)null) }, recorded.Select(r => (r.WindowsUser, r.Email)));
        Assert.NotNull(recorded[1].Reason);
        Assert.Empty(await _baselines.GetResolutionsAsync(_pc2.Id, CancellationToken.None));
    }

    // --- 5. Results per person ------------------------------------------------------------------------------

    [Fact]
    public async Task APersonsResult_IsKept_OnlyForAPerPersonItem_AndSomeoneResolvedHere()
    {
        await People("SERIAL-1", new SessionInput("ana", Ana));

        await Report("SERIAL-1",
            Signature("Compliant", "ANA@example.com"),
            // Not resolved on this host: dropped.
            Signature("Compliant", Ben),
            // A machine item never takes a subject: dropped.
            new ItemResultInput("copilot.off", "Compliant", "1", null, Ana),
            // The host's own result for the machine item: kept, as before.
            new ItemResultInput("copilot.off", "Drifted", "0", null));

        var rows = await Rows(_pc1);
        Assert.Equal(
            new[] { ("copilot.off", "", BaselineItemState.Drifted), ("outlook.signature", Ana, BaselineItemState.Compliant) },
            rows.OrderBy(r => r.ItemKey).Select(r => (r.ItemKey, r.Subject, r.State)));

        // The person's row comes back to the agent under the person, and never in the host's own states.
        var person = Assert.Single((await People("SERIAL-1", new SessionInput("ana", Ana))).People);
        Assert.Equal(new PersonItemStateDto(_versionId, "outlook.signature", "Compliant", null), Assert.Single(person.States));
        var hostStates = Assert.Single((await new GetAgentBaselinesQueryHandler(_baselines, _hosts, _applications)
            .Handle(new GetAgentBaselinesQuery("SERIAL-1"), CancellationToken.None)).Baselines).States;
        Assert.Equal(new[] { "copilot.off" }, hostStates.Select(s => s.Key));
    }

    [Fact]
    public async Task APersonResolvedMoreThanADayAgo_CannotBeReportedOn()
    {
        await People("SERIAL-1", new SessionInput("ana", Ana));
        var resolution = Assert.Single(await _baselines.GetResolutionsAsync(_pc1.Id, CancellationToken.None));
        _context.Entry(resolution).Property(r => r.LastSeenUtc).CurrentValue = DateTimeOffset.UtcNow.AddHours(-25);
        await _context.SaveChangesAsync();

        await Report("SERIAL-1", Signature("Compliant", Ana));

        Assert.DoesNotContain(await Rows(_pc1), r => r.Subject == Ana);
    }

    [Fact]
    public async Task AnotherHostsPerson_CannotBeReportedOn()
    {
        await Assign(App, allWindows: true);
        await People("SERIAL-1", new SessionInput("ana", Ana));

        await Report("SERIAL-2", Signature("Compliant", Ana));

        Assert.Empty(await Rows(_pc2));
    }

    // --- 6. Counts -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task AHostCountsAsTheWorstOfItsPeople()
    {
        await People("SERIAL-1", new SessionInput("ana", Ana), new SessionInput("ben.ex", null));
        await Report("SERIAL-1", Signature("Compliant", Ana), Signature("Drifted", Ben));

        Assert.Equal(new ComplianceCountsDto(0, 1, 0, 0, 0), await SignatureCounts());

        await Report("SERIAL-1", Signature("Error", Ana, "access denied"));
        Assert.Equal(new ComplianceCountsDto(0, 0, 0, 1, 0), await SignatureCounts());

        var host = await new GetBaselineHostQueryHandler(_baselines, _hosts).Handle(new GetBaselineHostQuery(_baselineId, _pc1.Id), CancellationToken.None);
        Assert.Equal(BaselineItemState.Error, host.Items.Single(i => i.Item.Key == "outlook.signature").State);
    }

    [Fact]
    public async Task AHostWithNobodyWithACopySignedIn_IsNotApplicable_WithTheReason()
    {
        Assert.Equal(new ComplianceCountsDto(0, 0, 0, 0, 1), await SignatureCounts());

        await People("SERIAL-1", new SessionInput("cy", null));

        Assert.Equal(new ComplianceCountsDto(0, 0, 1, 0, 0), await SignatureCounts());
        var own = Assert.Single(await Rows(_pc1));
        Assert.Equal(("", ResolveBaselinePeopleCommandHandler.NobodySignedIn), (own.Subject, own.Detail));
    }

    /// <summary>An agent without part 4 reports the new kind as an error under the host itself; once it updates
    /// and calls the people route, that error gives way.</summary>
    [Fact]
    public async Task AnOldAgentsError_Counts_UntilTheAgentAsksForItsPeople()
    {
        await Report("SERIAL-1", Signature("Error", null, "unknown item kind 'outlook-signature'"));
        Assert.Equal(new ComplianceCountsDto(0, 0, 0, 1, 0), await SignatureCounts());

        await People("SERIAL-1", new SessionInput("ana", Ana));
        Assert.Equal(new ComplianceCountsDto(0, 0, 0, 0, 1), await SignatureCounts());

        await Report("SERIAL-1", Signature("Compliant", Ana));
        Assert.Equal(new ComplianceCountsDto(1, 0, 0, 0, 0), await SignatureCounts());
    }

    [Fact]
    public async Task APersonWhoLostTheirCopy_DropsOutOfTheCount()
    {
        await People("SERIAL-1", new SessionInput("ana", Ana), new SessionInput("ben.ex", null));
        await Report("SERIAL-1", Signature("Compliant", Ana), Signature("Drifted", Ben));

        _signatures.Remove((await _signatures.GetAllAsync(CancellationToken.None)).Where(s => s.Email == Ben));
        await _context.SaveChangesAsync();

        Assert.Equal(new ComplianceCountsDto(1, 0, 0, 0, 0), await SignatureCounts());
    }

    // --- 8. The drill-down -------------------------------------------------------------------------------------

    [Fact]
    public async Task TheDrillDown_ListsEverySessionWithItsResolutionAndState()
    {
        await People("SERIAL-1", new SessionInput("ana", Ana), new SessionInput("cy", null));
        await Report("SERIAL-1", Signature("Drifted", Ana, "an account still has another default"));

        var rows = await new GetBaselinePeopleQueryHandler(_baselines, _hosts, _applications, _signatures)
            .Handle(new GetBaselinePeopleQuery(_baselineId), CancellationToken.None);

        Assert.Equal(2, rows.Count);
        var ana = rows[0];
        Assert.Equal(("pc-1", "ana", Ana, "gcpw", "outlook.signature", BaselineItemState.Drifted), (ana.Hostname, ana.WindowsUser, ana.Email, ana.ResolvedBy, ana.ItemKey, ana.State));
        Assert.NotNull(ana.CopyRenderedAtUtc);
        Assert.Equal("an account still has another default", ana.Detail);
        var cy = rows[1];
        Assert.Equal(("cy", (string?)null, (BaselineItemState?)null), (cy.WindowsUser, cy.Email, cy.State));
        Assert.NotNull(cy.Reason);
    }
}
