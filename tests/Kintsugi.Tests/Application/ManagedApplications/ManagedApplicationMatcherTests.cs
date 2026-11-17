using Kintsugi.Application.ManagedApplications;
using Kintsugi.Domain.Entities;

namespace Kintsugi.Tests.Application.ManagedApplications;

public class ManagedApplicationMatcherTests
{
    private static ManagedApplication Row(string name, string owner) => ManagedApplication.Create(name, owner);

    [Fact]
    public void AnExactNameWinsOverAPatternThatAlsoMatches()
    {
        var rows = new[] { Row("CrowdStrike*", "CrowdStrike"), Row("CrowdStrike Windows Sensor", "NinjaOne") };

        Assert.Equal("NinjaOne", ManagedApplicationMatcher.Find(rows, "crowdstrike windows sensor")!.Owner);
        Assert.Equal("CrowdStrike", ManagedApplicationMatcher.Find(rows, "CrowdStrike Falcon")!.Owner);
    }

    [Fact]
    public void WhenPatternsOverlap_TheMoreSpecificOneWins_WhateverOrderTheRowsComeIn()
    {
        var broad = Row("Microsoft*", "Microsoft Store");
        var specific = Row("Microsoft.VCLibs.*", "Microsoft 365");

        Assert.Equal("Microsoft 365", ManagedApplicationMatcher.Find(new[] { broad, specific }, "Microsoft.VCLibs.140")!.Owner);
        Assert.Equal("Microsoft 365", ManagedApplicationMatcher.Find(new[] { specific, broad }, "Microsoft.VCLibs.140")!.Owner);
        Assert.Equal("Microsoft Store", ManagedApplicationMatcher.Find(new[] { specific, broad }, "Microsoft Edge")!.Owner);
    }

    [Fact]
    public void TwoPatternsOfTheSameSpecificityThatBothMatch_ResolveByNameNotByOrder()
    {
        // Four literal characters each, and "Zoom Plus" matches both: the tie is broken by the pattern's name.
        var prefix = Row("Zoom*", "OwnerPrefix");
        var suffix = Row("*Plus", "OwnerSuffix");

        Assert.Equal("OwnerSuffix", ManagedApplicationMatcher.Find(new[] { prefix, suffix }, "Zoom Plus")!.Owner);
        Assert.Equal("OwnerSuffix", ManagedApplicationMatcher.Find(new[] { suffix, prefix }, "Zoom Plus")!.Owner);
        Assert.Equal("OwnerPrefix", ManagedApplicationMatcher.Find(new[] { suffix, prefix }, "Zoom Rooms")!.Owner);
    }

    [Fact]
    public void ANameNothingCoversIsNotMatched() =>
        Assert.Null(ManagedApplicationMatcher.Find(new[] { Row("CrowdStrike*", "CrowdStrike"), Row("Zoom", "Jamf Pro") }, "Slack"));

    [Fact]
    public void TheLookupAnswersTheSameWhateverOrderTheEntriesComeIn()
    {
        var entries = new[]
        {
            new KeyValuePair<string, string>("Microsoft*", "Broad"),
            new KeyValuePair<string, string>("Microsoft.VCLibs.*", "Specific"),
        };

        Assert.Equal("Specific", new ManagedOwnerLookup(entries)["Microsoft.VCLibs.140"]);
        Assert.Equal("Specific", new ManagedOwnerLookup(entries.Reverse())["Microsoft.VCLibs.140"]);
        Assert.Equal("Broad", new ManagedOwnerLookup(entries.Reverse())["Microsoft Edge"]);
    }
}
