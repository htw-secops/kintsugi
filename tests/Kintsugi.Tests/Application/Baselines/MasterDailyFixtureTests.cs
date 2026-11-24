using System.Text.Json;
using Kintsugi.Application.Baselines;
using Kintsugi.Application.Baselines.Commands.CreateBaseline;
using Kintsugi.WebApi.Controllers;

namespace Kintsugi.Tests.Application.Baselines;

/// <summary>
/// <c>baselines/master-daily.json</c> and <c>baselines/idle-lock.json</c> are pasted into the Baselines screen
/// as-is, so each has to be exactly what <c>POST /api/admin/baselines</c> accepts. Master Daily is sections
/// 1–12 and 17, machine scope only, audit except section 17, the enforce pilot. The idle lock is IP-926's
/// pilot: the lock and password on wake, enforced, for one device first.
/// </summary>
public class MasterDailyFixtureTests
{
    private static CreateBaselineRequest Load(string file)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Kintsugi.sln")))
        {
            directory = directory.Parent;
        }

        var json = File.ReadAllText(Path.Combine(directory!.FullName, "baselines", file));
        return JsonSerializer.Deserialize<CreateBaselineRequest>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
    }

    private static void AssertTheCreateValidatorAccepts(CreateBaselineRequest fixture)
    {
        var result = new CreateBaselineCommandValidator().Validate(new CreateBaselineCommand(fixture.Name, fixture.Items));
        Assert.True(result.IsValid, string.Join("; ", result.Errors.Select(e => e.ErrorMessage)));
    }

    [Fact]
    public void HoldsMasterDailys32MachineSettings_AuditingAllButSection17_AndPassesTheCreateValidator()
    {
        var fixture = Load("master-daily.json");

        Assert.Equal(32, fixture.Items.Count);
        Assert.All(fixture.Items, i =>
        {
            Assert.Equal("setting", i.Kind);
            Assert.Equal("machine", i.Scope);
            Assert.Equal(i.Key == "chrome.window-occlusion-off" ? "enforce" : "audit", i.Mode);
        });

        AssertTheCreateValidatorAccepts(fixture);
    }

    /// <summary>Section 2's token is a secret: the fixture names it and carries no value.</summary>
    [Fact]
    public void TheChromeTokenNamesASecretAndCarriesNoValue()
    {
        var secret = Assert.Single(Load("master-daily.json").Items, i => i.Secret is not null);
        Assert.Equal("CloudManagementEnrollmentToken", secret.ValueName);
        Assert.Null(secret.Data);
    }

    /// <summary>
    /// IP-926: Master Daily's lock items wrote values that lock nothing, and audit reported them compliant. Each
    /// lock item now names a value Windows acts on, and each would be enforceable as it stands, so the pilot's
    /// enforce version differs from it only in mode.
    /// </summary>
    [Theory]
    [InlineData("master-daily.json")]
    [InlineData("idle-lock.json")]
    public void TheIdleLockItems_NameValuesWindowsActsOn(string file)
    {
        var items = Load(file).Items;

        var idleLock = Assert.Single(items, i => i.Key == "lock.machine-inactivity-limit");
        Assert.Equal(("InactivityTimeoutSecs", "600"), (idleLock.ValueName, idleLock.Data));
        foreach (var key in new[] { "lock.machine-inactivity-limit", "wake.password-on-ac", "wake.password-on-battery" })
        {
            var item = Assert.Single(items, i => i.Key == key);
            Assert.True(BaselineItemRules.IsEnforceable(item with { Mode = "enforce" }), key);
        }

        Assert.DoesNotContain(items, i => i.Path.Contains(@"PolicyManager\default\DeviceLock", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(items, i => i.ValueName == "MaxInactivityDuration");
        Assert.DoesNotContain(items, i => i.Path.Contains("0e796bdb-5952-4e82-9c48-3d5f20595d87", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void TheIdleLockPilot_EnforcesOnlyItsThreeItems_AndPassesTheCreateValidator()
    {
        var fixture = Load("idle-lock.json");

        Assert.Equal(
            new[] { "lock.machine-inactivity-limit", "wake.password-on-ac", "wake.password-on-battery" },
            fixture.Items.Select(i => i.Key).Order());
        Assert.All(fixture.Items, i => Assert.Equal("enforce", i.Mode));

        AssertTheCreateValidatorAccepts(fixture);
    }
}
