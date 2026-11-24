using Kintsugi.Application.Baselines;
using static Kintsugi.Tests.Application.Baselines.BaselineTestData;

namespace Kintsugi.Tests.Application.Baselines;

/// <summary>The <c>google-mdm</c> item's rules: it names a policy and a staleness threshold, and nothing about what
/// the agent reads or does, which is fixed in <c>google_mdm.rs</c>.</summary>
public class GoogleMdmItemTests
{
    private static bool Valid(BaselineItem item) => new BaselineItemValidator().Validate(item).IsValid;

    [Theory]
    [InlineData("audit", "3")]
    [InlineData("audit", "14")]
    [InlineData("enforce", "14")]
    [InlineData("enforce", "90")]
    public void AValidItem_IsAccepted(string mode, string staleDays)
    {
        Assert.True(Valid(GoogleMdm(staleDays, mode)));
        Assert.True(new BaselineItemsValidator().Validate(new[] { GoogleMdm(staleDays, mode), Setting("copilot.off"), Fact("edition") }).IsValid);
    }

    public static IEnumerable<object[]> Broken() => new[]
    {
        new object[] { "user scope", GoogleMdm() with { Scope = "user", Hive = "HKCU" } },
        new object[] { "HKCU", GoogleMdm() with { Hive = "HKCU" } },
        new object[] { "another path", GoogleMdm() with { Path = @"SOFTWARE\Microsoft\Provisioning\OMADM\Accounts" } },
        new object[] { "path in other case", GoogleMdm() with { Path = @"software\microsoft\enrollments" } },
        new object[] { "a protected path", GoogleMdm() with { Path = @"SAM\SAM" } },
        new object[] { "a value name", GoogleMdm() with { ValueName = "UPN" } },
        new object[] { "a value type", GoogleMdm() with { ValueType = "dword" } },
        new object[] { "a secret", GoogleMdm() with { Secret = "MDM" } },
        new object[] { "a signature name", GoogleMdm() with { SignatureName = "Example" } },
        new object[] { "no mode", GoogleMdm() with { Mode = null } },
        new object[] { "another mode", GoogleMdm() with { Mode = "unenroll" } },
    };

    [Theory]
    [MemberData(nameof(Broken))]
    public void AnythingElse_IsRefused(string why, BaselineItem item) => Assert.False(Valid(item), why);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("2")]
    [InlineData("91")]
    [InlineData("014")]
    [InlineData(" 14")]
    [InlineData("14 ")]
    [InlineData("+14")]
    [InlineData("-14")]
    [InlineData("14.0")]
    [InlineData("fourteen")]
    public void StaleDays_OutsideTheRange_OrNotAWholeNumber_IsRefused(string? staleDays) =>
        Assert.False(Valid(GoogleMdm() with { Data = staleDays }));

    [Fact]
    public void ABaseline_HoldsAtMostOneGoogleMdmItem()
    {
        var result = new BaselineItemsValidator().Validate(new[] { GoogleMdm(), GoogleMdm() with { Key = "google.mdm.2" } });

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.ErrorMessage.Contains("at most one google-mdm"));
    }

    [Fact]
    public void AGoogleMdmItem_IsJudgedOncePerHost()
    {
        var item = GoogleMdm();

        Assert.True(item.IsGoogleMdm);
        Assert.True(item.IsMachineJudged);
        Assert.True(item.IsJudged);
        Assert.False(item.IsPerPerson);
        Assert.False(item.IsSetting);
    }

    [Fact]
    public void AnEnforcedGoogleMdmItem_DoesNotWidenWhatASettingCanEnforce() =>
        Assert.False(BaselineItemRules.IsEnforceable(GoogleMdm(mode: "enforce")));
}
