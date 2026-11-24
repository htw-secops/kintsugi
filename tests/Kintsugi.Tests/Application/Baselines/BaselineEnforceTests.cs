using System.Text.RegularExpressions;
using Kintsugi.Application.Baselines;
using static Kintsugi.Tests.Application.Baselines.BaselineTestData;

namespace Kintsugi.Tests.Application.Baselines;

/// <summary>
/// <c>enforce</c> is accepted only for a value on <see cref="BaselineItemRules.EnforceableValues"/>, and
/// that list is the same one the Windows agent compiles in (<c>baseline::ENFORCEABLE</c>).
/// </summary>
public class BaselineEnforceTests
{
    private static BaselineItem ChromeOcclusion(string mode = "enforce") =>
        Setting("chrome.window-occlusion-off", data: "0", valueName: "WindowOcclusionEnabled")
            with { Path = @"SOFTWARE\Policies\Google\Chrome", Mode = mode };

    [Fact]
    public void Validator_AcceptsEnforce_ForAValueOnTheAllowlist_InAnyCase()
    {
        Assert.True(new BaselineItemValidator().Validate(ChromeOcclusion()).IsValid);
        Assert.True(new BaselineItemValidator().Validate(ChromeOcclusion() with { Path = @"software\policies\google\chrome", ValueName = "windowocclusionenabled" }).IsValid);
        Assert.True(new BaselineItemValidator().Validate(ChromeOcclusion("audit")).IsValid);
    }

    /// <summary>Each of these is a SYSTEM write that runs code or weakens the machine; none is on the list.</summary>
    [Theory]
    [InlineData(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", "Updater", "string")]
    [InlineData(@"SYSTEM\CurrentControlSet\Services\Spooler", "ImagePath", "expandString")]
    [InlineData(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\sethc.exe", "Debugger", "string")]
    [InlineData(@"SOFTWARE\Policies\Google\Chrome\ExtensionInstallForcelist", "1", "string")]
    [InlineData(@"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate", "WUServer", "string")]
    [InlineData(@"SOFTWARE\Policies\Google\Chrome", "WindowOcclusionEnabled", "string")]
    public void Validator_RefusesEnforce_ForAnythingElse(string path, string valueName, string valueType)
    {
        var item = Setting("x", data: "1", valueName: valueName) with { Path = path, ValueType = valueType, Mode = "enforce" };

        var result = new BaselineItemValidator().Validate(item);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.ErrorMessage.Contains("EnforceableValues"));
        Assert.True(new BaselineItemValidator().Validate(item with { Mode = "audit" }).IsValid);
    }

    [Fact]
    public void Validator_RefusesEnforce_ForASecret_AndAnyModeButAuditOrEnforce()
    {
        Assert.False(new BaselineItemValidator().Validate(ChromeOcclusion() with { Data = null, Secret = "OCCLUSION" }).IsValid);
        Assert.False(new BaselineItemValidator().Validate(ChromeOcclusion("set")).IsValid);
    }

    private const string SystemPolicies = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System";
    private const string ConsoleLock = @"SOFTWARE\Policies\Microsoft\Power\PowerSettings\0e796bdb-100d-47d6-a2d5-f7d2daa51f51";

    private static BaselineItem IdleLock(string data, string mode = "enforce") =>
        Setting("lock.machine-inactivity-limit", data: data, valueName: "InactivityTimeoutSecs") with { Path = SystemPolicies, Mode = mode };

    /// <summary>IP-926: the idle lock and password on wake can be enforced, at a value in range.</summary>
    [Theory]
    [InlineData(SystemPolicies, "InactivityTimeoutSecs", "60")]
    [InlineData(SystemPolicies, "InactivityTimeoutSecs", "600")]
    [InlineData(SystemPolicies, "InactivityTimeoutSecs", "900")]
    [InlineData(ConsoleLock, "ACSettingIndex", "1")]
    [InlineData(ConsoleLock, "DCSettingIndex", "1")]
    [InlineData(@"SOFTWARE\Policies\Google\Chrome", "WindowOcclusionEnabled", "1")]
    public void Validator_AcceptsEnforce_WithinTheValuesRange(string path, string valueName, string data)
    {
        var item = Setting("x", data: data, valueName: valueName) with { Path = path, Mode = "enforce" };

        var result = new BaselineItemValidator().Validate(item);

        Assert.True(result.IsValid, string.Join("; ", result.Errors.Select(e => e.ErrorMessage)));
    }

    /// <summary>
    /// A value's "off" is outside its range: 0 removes the idle lock, a lock that waits hours is no lock, and
    /// password-on-wake 0 stops asking. A signed document must not be able to enforce any of them; it can still
    /// audit them, since auditing writes nothing.
    /// </summary>
    [Theory]
    [InlineData(SystemPolicies, "InactivityTimeoutSecs", "0", "60 to 900 seconds")]
    [InlineData(SystemPolicies, "InactivityTimeoutSecs", "59", "60 to 900 seconds")]
    [InlineData(SystemPolicies, "InactivityTimeoutSecs", "901", "60 to 900 seconds")]
    [InlineData(SystemPolicies, "InactivityTimeoutSecs", "599940", "60 to 900 seconds")]
    [InlineData(ConsoleLock, "ACSettingIndex", "0", "only at 1,")]
    [InlineData(ConsoleLock, "DCSettingIndex", "0", "only at 1,")]
    [InlineData(@"SOFTWARE\Policies\Google\Chrome", "WindowOcclusionEnabled", "2", "0 to 1")]
    public void Validator_RefusesEnforce_OutsideTheValuesRange(string path, string valueName, string data, string range)
    {
        var item = Setting("x", data: data, valueName: valueName) with { Path = path, Mode = "enforce" };

        var result = new BaselineItemValidator().Validate(item);

        Assert.False(result.IsValid);
        var error = Assert.Single(result.Errors);
        Assert.Contains(range, error.ErrorMessage);
        Assert.Contains($"'{data}'", error.ErrorMessage);
        Assert.True(new BaselineItemValidator().Validate(item with { Mode = "audit" }).IsValid);
    }

    [Fact]
    public void IsEnforceable_NeedsDataInRange_AndFindEnforceable_DoesNot()
    {
        Assert.True(BaselineItemRules.IsEnforceable(IdleLock("600")));
        Assert.False(BaselineItemRules.IsEnforceable(IdleLock("0")));
        Assert.NotNull(BaselineItemRules.FindEnforceable(IdleLock("0")));
        Assert.Equal("Interactive logon: Machine inactivity limit", BaselineItemRules.FindEnforceable(IdleLock("0"))!.Title);
    }

    /// <summary>Only a plain decimal is in range: no sign, no hex, nothing a different parser might read differently.</summary>
    [Theory]
    [InlineData("600", true)]
    [InlineData("0600", true)]
    [InlineData("+600", false)]
    [InlineData("0x258", false)]
    [InlineData(" 600", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Allows_ReadsOnlyPlainDecimals(string? data, bool allowed)
    {
        var lockValue = BaselineItemRules.EnforceableValues.Single(v => v.ValueName == "InactivityTimeoutSecs");

        Assert.Equal(allowed, lockValue.Allows(data));
    }

    [Fact]
    public void Allows_RefusesATypeWithNoRangeRule()
    {
        Assert.False(new EnforceableValue(@"SOFTWARE\Example", "Name", "string", 0, 9, "Example", "").Allows("1"));
    }

    /// <summary>
    /// The agent's list is the one that actually stops a write, so it must never be wider than this one,
    /// nor narrower (a value the server lets you enforce that the agent then refuses only drifts). That holds
    /// for each value's range as much as for its name.
    /// </summary>
    [Fact]
    public void TheAgentsAllowlist_IsExactlyTheServers()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Kintsugi.sln")))
        {
            directory = directory.Parent;
        }

        var source = File.ReadAllText(Path.Combine(directory!.FullName, "clients", "windows-agent", "src", "baseline.rs"));
        var block = Regex.Match(source, @"const ENFORCEABLE: \[\(&str, &str, &str, u64, u64\); (?<count>\d+)\] = \[(?<entries>.*?)\];", RegexOptions.Singleline);
        Assert.True(block.Success, "baseline.rs no longer declares ENFORCEABLE in the shape this test reads");

        var agent = Regex.Matches(block.Groups["entries"].Value,
                @"\(r""(?<path>[^""]+)"", ""(?<name>[^""]+)"", ""(?<type>[^""]+)"", (?<min>\d+), (?<max>\d+)\)")
            .Select(m => (m.Groups["path"].Value, m.Groups["name"].Value, m.Groups["type"].Value,
                ulong.Parse(m.Groups["min"].Value), ulong.Parse(m.Groups["max"].Value)))
            .ToHashSet();
        var server = BaselineItemRules.EnforceableValues.Select(v => (v.Path, v.ValueName, v.ValueType, v.Min, v.Max)).ToHashSet();

        Assert.NotEmpty(agent);
        Assert.Equal(int.Parse(block.Groups["count"].Value), agent.Count);
        Assert.Equal(server, agent);
    }

    /// <summary>Each range is one the value can hold, low to high: a dword entry above uint's ceiling could never match.</summary>
    [Fact]
    public void EveryRange_IsOrdered_AndFitsItsType()
    {
        Assert.All(BaselineItemRules.EnforceableValues, v =>
        {
            Assert.True(v.Min <= v.Max, v.ValueName);
            Assert.True(v.ValueType != "dword" || v.Max <= uint.MaxValue, v.ValueName);
            Assert.False(string.IsNullOrWhiteSpace(v.Title), v.ValueName);
        });
    }
}
