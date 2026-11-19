using Kintsugi.Application.ManagedApplications;

namespace Kintsugi.Tests.Application;

public class ManagedOwnerLookupTests
{
    private static ManagedOwnerLookup Lookup(params (string Name, string Owner)[] entries) =>
        new(entries.Select(e => new KeyValuePair<string, string>(e.Name, e.Owner)));

    [Fact]
    public void AnExactNameMatchesWithoutRegardToCase()
    {
        var lookup = Lookup(("Falcon Sensor", "CrowdStrike"));

        Assert.True(lookup.TryGetValue("falcon sensor", out var owner));
        Assert.Equal("CrowdStrike", owner);
    }

    [Theory]
    [InlineData("Microsoft.VCLibs.140.00", true)]
    [InlineData("microsoft.vclibs.140.00.uwpdesktop", true)]
    [InlineData("Microsoft.VCLibs", false)]
    [InlineData("Contoso.Microsoft.VCLibs.140.00", false)]
    public void ATrailingStarMatchesAnyRestOfTheName(string name, bool expected)
    {
        var lookup = Lookup(("Microsoft.VCLibs.*", "Microsoft Store"));

        Assert.Equal(expected, lookup.ContainsKey(name));
    }

    [Fact]
    public void AStarAtTheEndOfAWordMatchesTheWholeFamily()
    {
        var lookup = Lookup(("CrowdStrike*", "CrowdStrike"));

        Assert.True(lookup.ContainsKey("CrowdStrike Falcon"));
        Assert.True(lookup.ContainsKey("CrowdStrike Windows Sensor"));
        Assert.False(lookup.ContainsKey("Not CrowdStrike"));
    }

    [Fact]
    public void OtherRegexCharactersInAPatternAreLiteral()
    {
        var lookup = Lookup(("Jamf Self Service+", "Jamf Pro"), ("a.b*", "X"));

        Assert.True(lookup.ContainsKey("Jamf Self Service+"));
        Assert.False(lookup.ContainsKey("Jamf Self Servicee"));
        Assert.False(lookup.ContainsKey("axb1"));
        Assert.True(lookup.ContainsKey("a.b1"));
    }

    [Fact]
    public void AnExactNameWinsOverAPattern()
    {
        var lookup = Lookup(("Microsoft.UI.Xaml.*", "Microsoft Store"), ("Microsoft.UI.Xaml.2.8", "Someone Else"));

        Assert.Equal("Someone Else", lookup["Microsoft.UI.Xaml.2.8"]);
        Assert.Equal("Microsoft Store", lookup["Microsoft.UI.Xaml.2.7"]);
    }

    [Fact]
    public void GetValueOrDefaultAndTheIndexerBehaveLikeADictionary()
    {
        var lookup = Lookup(("Copilot", "Microsoft Store"));

        Assert.Equal("Microsoft Store", lookup.GetValueOrDefault("copilot"));
        Assert.Null(lookup.GetValueOrDefault("Other"));
        Assert.Throws<KeyNotFoundException>(() => lookup["Other"]);
    }
}
