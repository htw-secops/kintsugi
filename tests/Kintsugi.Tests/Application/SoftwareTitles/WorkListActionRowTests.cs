using Kintsugi.Application.UpgradePaths;
using Kintsugi.Application.UpgradePaths.Queries.GetUpgradeStatuses;
using Kintsugi.Domain.Entities;
using Kintsugi.Domain.Enums;

namespace Kintsugi.Tests.Application.SoftwareTitles;

/// <summary>
/// The action rows <see cref="GetUpgradeStatusesQueryHandler"/> appends to an agent's work list: which
/// mode and detection state produce one, what it carries, and when it is withheld.
/// </summary>
public class WorkListActionRowTests
{
    private readonly SoftwareTestbed _bed = new();
    private static readonly string WingetBucket = PlatformBucket.ForPackageManager("winget");

    private async Task<IReadOnlyList<UpgradeStatusDto>> WorkListAsync() =>
        await new GetUpgradeStatusesQueryHandler(_bed.UpgradePaths, _bed.Hosts, _bed.Installed, _bed.Titles, _bed.Secrets)
            .Handle(new GetUpgradeStatusesQuery("SERIAL-1"), CancellationToken.None);

    /// <summary>winget on the host, and its bucket's script signed with the action modes in it.</summary>
    private void WingetReady()
    {
        _bed.Install("winget");
        _bed.Script("winget", WingetBucket, WingetUpgradeScript.Build(), signed: true);
    }

    private void ZohoScript(bool signed = true, string script = SoftwareTestbed.ActionScript) =>
        _bed.Script("Zoho Assist", "Windows", script, signed, latestVersion: "1.2.3");

    [Fact]
    public async Task AHostInMaintenance_GetsAnEmptyWorkList()
    {
        WingetReady();
        _bed.WingetTitle(SoftwareMode.Required);
        Assert.NotEmpty(await WorkListAsync());

        _bed.Host.StartMaintenance(DateTimeOffset.UtcNow, until: null, "replacing the disk");

        Assert.Empty(await WorkListAsync());
    }

    [Fact]
    public async Task AHostGetsItsRowsBack_OnceMaintenanceHasEnded()
    {
        WingetReady();
        _bed.WingetTitle(SoftwareMode.Required);
        _bed.Host.StartMaintenance(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(1), reason: null);
        Assert.Empty(await WorkListAsync());

        // The end time passes by itself; nothing clears it. StartMaintenance refuses a past end, so move it directly.
        typeof(Host).GetProperty(nameof(Host.MaintenanceUntilUtc))!.SetValue(_bed.Host, DateTimeOffset.UtcNow.AddMinutes(-1));

        Assert.NotEmpty(await WorkListAsync());
    }

    [Fact]
    public async Task Required_WhenAbsent_AddsAnInstallRow_CarryingTheContractFields()
    {
        WingetReady();
        _bed.Secret("TEST_SECRET_A");
        var title = _bed.WingetTitle(SoftwareMode.Required, "TEST_SECRET_A");

        var row = Assert.Single(await WorkListAsync(), r => r.Action is not null);

        Assert.Equal("Greenshot", row.ApplicationName);
        Assert.Equal("Install", row.Action);
        Assert.False(row.SelfService);
        Assert.False(row.UpdateAvailable);
        Assert.Equal("", row.InstalledVersion);
        Assert.Equal(UpgradeMethod.Script, row.Method);
        Assert.Equal(UpgradePathStatus.Found, row.Status);
        Assert.Equal("Greenshot.Greenshot", row.ApplicationIdentifier);
        Assert.Equal("winget", row.PackageManager);
        Assert.Equal(WingetUpgradeScript.Build(), row.Script);
        Assert.Equal("script-sig", row.ScriptSignature);
        Assert.Equal(title.ActionSignature, row.ActionSignature);
        Assert.Equal("pc-1", row.Hostname);
        Assert.Equal("SERIAL-1", row.SerialNumber);
        var secret = Assert.Single(row.Secrets!);
        Assert.Equal(("TEST_SECRET_A", "TEST_SECRET_VALUE", "secret-sig"), (secret.Name, secret.Value, secret.Signature));
    }

    [Fact]
    public async Task Required_WhenDetected_AddsNothing()
    {
        WingetReady();
        _bed.Install("Greenshot 1.3", "Greenshot.Greenshot");
        _bed.WingetTitle(SoftwareMode.Required);

        Assert.DoesNotContain(await WorkListAsync(), r => r.Action is not null);
    }

    [Fact]
    public async Task SelfService_WhenAbsent_AddsAnInstallRowMarkedSelfService()
    {
        ZohoScript();
        _bed.CustomTitle(SoftwareMode.SelfService);

        var row = Assert.Single(await WorkListAsync());

        Assert.Equal("Install", row.Action);
        Assert.True(row.SelfService);
        Assert.Equal("Zoho Assist", row.ApplicationIdentifier);
        Assert.Null(row.PackageManager);
        Assert.Equal("1.2.3", row.LatestVersion);
    }

    [Fact]
    public async Task SelfService_WhenDetected_AddsNothing()
    {
        ZohoScript();
        _bed.Install("Zoho Assist Unattended Agent");
        _bed.CustomTitle(SoftwareMode.SelfService, detectAs: "Zoho Assist");

        Assert.DoesNotContain(await WorkListAsync(), r => r.Action is not null);
    }

    [Fact]
    public async Task Prohibited_WhenDetected_AddsAnUninstallRow_AndDropsTheDetectedAppsUpgradeRows()
    {
        // The detected app has an upgrade row of its own (an older version, a newer one available).
        // A prohibited app is not patched, and no two rows share a name.
        ZohoScript();
        _bed.Install("Zoho Assist");
        _bed.Install("Zoho Assist Helper");
        _bed.Script("Zoho Assist Helper", "Windows", SoftwareTestbed.UpgradeOnlyScript, signed: true, latestVersion: "9.0");
        _bed.CustomTitle(SoftwareMode.Prohibited, detectAs: "Zoho Assist");

        var rows = await WorkListAsync();

        var row = Assert.Single(rows);
        Assert.Equal("Uninstall", row.Action);
        Assert.Equal("Zoho Assist", row.ApplicationName);
        Assert.False(row.UpdateAvailable);
    }

    [Fact]
    public async Task Prohibited_DropsUpgradeRows_EvenWhenItsOwnScriptIsNotReady()
    {
        _bed.Install("Zoho Assist");
        ZohoScript(signed: false);
        _bed.CustomTitle(SoftwareMode.Prohibited);

        Assert.Empty(await WorkListAsync());
    }

    [Fact]
    public async Task Prohibited_WhenAbsent_AddsNothing()
    {
        ZohoScript();
        _bed.CustomTitle(SoftwareMode.Prohibited);

        Assert.Empty(await WorkListAsync());
    }

    [Fact]
    public async Task AnUnsignedScript_IsNotServed()
    {
        ZohoScript(signed: false);
        _bed.CustomTitle(SoftwareMode.Required);

        Assert.Empty(await WorkListAsync());
    }

    [Fact]
    public async Task ASignedScriptWithoutTheMode_IsNotServed()
    {
        ZohoScript(script: SoftwareTestbed.UpgradeOnlyScript);
        _bed.CustomTitle(SoftwareMode.Required);

        Assert.Empty(await WorkListAsync());
    }

    [Fact]
    public async Task ASignedManagerScriptFromBeforeActionModes_IsNotServed()
    {
        _bed.Install("winget");
        _bed.Script("winget", WingetBucket, SoftwareTestbed.UpgradeOnlyScript, signed: true);
        _bed.WingetTitle(SoftwareMode.Required);

        Assert.DoesNotContain(await WorkListAsync(), r => r.Action is not null);
    }

    [Fact]
    public async Task ATitleWithoutAnActionSignature_IsNotServed()
    {
        ZohoScript();
        _bed.AddTitle(SoftwareTitle.Create("Zoho Assist", "Windows", null, "Windows", null, null, SoftwareMode.Required, [], "admin"), sign: false);

        Assert.Empty(await WorkListAsync());
    }

    [Fact]
    public async Task ATitleNamingASecretThatIsNotStored_IsNotServed()
    {
        ZohoScript();
        _bed.CustomTitle(SoftwareMode.Required, secrets: "TEST_SECRET_MISSING");

        Assert.Empty(await WorkListAsync());
    }

    [Fact]
    public async Task APackageManagerTitle_OnAHostWithoutTheManager_IsNotServed()
    {
        _bed.Script("winget", WingetBucket, WingetUpgradeScript.Build(), signed: true);
        _bed.WingetTitle(SoftwareMode.Required);

        Assert.Empty(await WorkListAsync());
    }

    [Fact]
    public async Task ATitleNotAssignedToTheHost_IsNotServed()
    {
        ZohoScript();
        _bed.AddTitle(SoftwareTitle.Create("Zoho Assist", "Windows", null, "Windows", null, null, SoftwareMode.Required, [], "admin"), assign: false);

        Assert.Empty(await WorkListAsync());
    }

    [Fact]
    public async Task ATitleForAnotherOs_IsNotServed_EvenWhenAssignedToAllHosts()
    {
        _bed.Script("Zoho Assist", "macOS", SoftwareTestbed.ActionScript, signed: true);
        var title = SoftwareTitle.Create("Zoho Assist", "macOS", null, "macOS", null, null, SoftwareMode.Required, [], "admin");
        title.Assign(true, [], "admin");
        _bed.AddTitle(title, assign: false);

        Assert.Empty(await WorkListAsync());
    }

    [Fact]
    public async Task ManagedApplication_IsNotConsulted_ForActionRows()
    {
        // ManagedApplication stops Kintsugi patching an app another tool updates. It says nothing about
        // whether the app should be installed, so an install row is still served.
        WingetReady();
        _bed.Context.ManagedApplications.Add(ManagedApplication.Create("Greenshot", "Another tool"));
        _bed.Context.SaveChanges();
        _bed.WingetTitle(SoftwareMode.Required);

        Assert.Contains(await WorkListAsync(), r => r.Action == "Install" && r.ApplicationName == "Greenshot");
    }

    [Fact]
    public async Task Secrets_RideOnlyOnActionRows()
    {
        WingetReady();
        _bed.Secret("TEST_SECRET_A");
        _bed.Install("Firefox");
        _bed.Script("Firefox", "Windows", SoftwareTestbed.UpgradeOnlyScript, signed: true, latestVersion: "130.0");
        _bed.WingetTitle(SoftwareMode.Required, "TEST_SECRET_A");

        var rows = await WorkListAsync();

        Assert.All(rows.Where(r => r.Action is null), r => Assert.Null(r.Secrets));
        Assert.NotEmpty(rows.Single(r => r.Action is not null).Secrets!);
        Assert.Contains(rows, r => r.ApplicationName == "Firefox" && r.Action is null);
    }

    [Fact]
    public async Task WithNoTitles_TheListIsTheUpgradeRowsAlone()
    {
        _bed.Install("Firefox");
        _bed.Script("Firefox", "Windows", SoftwareTestbed.UpgradeOnlyScript, signed: true, latestVersion: "130.0");

        var row = Assert.Single(await WorkListAsync());
        Assert.Null(row.Action);
        Assert.False(row.SelfService);
        Assert.Null(row.ActionSignature);
        Assert.Null(row.Secrets);
    }
}
