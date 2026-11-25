using Moq;
using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Application.PatchFailures;
using Kintsugi.Application.UpgradePaths;
using Kintsugi.Domain.Entities;
using Kintsugi.Domain.Enums;

namespace Kintsugi.Tests.Application.PatchFailures;

/// <summary>
/// The reconciler closing failures nothing else could: a stale registration beside a current one of the same
/// package, and a patch the agent will never run again. Each case is one a real winget host produced.
/// </summary>
public class PatchFailureSelfHealTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 26, 5, 0, 0, TimeSpan.Zero);
    private readonly Mock<IPatchFailureRepository> _failures = new();
    private readonly Mock<IInstalledApplicationRepository> _installed = new();
    private readonly Mock<IUpgradePathRepository> _paths = new();
    private readonly Guid _hostId = Guid.NewGuid();

    public PatchFailureSelfHealTests() =>
        _paths.Setup(r => r.GetAllForApplicationAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);

    private PatchFailureReconciler Create() => new(_failures.Object, _installed.Object, _paths.Object);

    private PatchFailure WingetFailure(string app, string? attempted, int failedDaysAgo = 2) =>
        PatchFailure.Open(_hostId, app, "pm:winget", "1.0", attempted, "exit 1", Now.AddDays(-failedDaysAgo));

    private InstalledApplication Winget() => new(_hostId, "winget", "1.29.380", "winget");

    private InstalledApplication Under(InstalledApplication manager, string name, string version, string? id)
    {
        var app = new InstalledApplication(_hostId, name, version, id);
        app.SetParent(manager.Id);
        return app;
    }

    private void Path(string app, string latest) =>
        _paths.Setup(r => r.GetAllForApplicationAsync(app, It.IsAny<CancellationToken>()))
            .ReturnsAsync([UpgradePath.Create(app, "pm:winget", UpgradePathStatus.Found, latest, UpgradeMethod.Script, null, null, null, null, null, script: "x", applicationIdentifier: app)]);

    private async Task<PatchFailureResolution> Reconcile(PatchFailure failure, params InstalledApplication[] inventory)
    {
        await Create().ReconcileAsync([failure], inventory, Now, CancellationToken.None);
        return failure.Resolution;
    }

    private const string OldVcRedist = "Microsoft Visual C++ 2015-2019 Redistributable (x64) - 14.28.29914";
    private const string NewVcRedist = "Microsoft Visual C++ v14 Redistributable (x64) - 14.52.37029";

    [Fact]
    public async Task AStaleRegistration_BesideACurrentOneOfTheSamePackage_ClosesAsAFalsePositive()
    {
        var winget = Winget();
        var failure = WingetFailure(OldVcRedist, "14.52.37029.0");
        Path(OldVcRedist, "14.52.37029.0");

        var resolution = await Reconcile(failure,
            winget,
            Under(winget, OldVcRedist, "14.28.29914.0", "Microsoft.VCRedist.2015+.x64"),
            Under(winget, NewVcRedist, "14.52.37029.0", "Microsoft.VCRedist.2015+.x64"));

        Assert.Equal(PatchFailureResolution.FalsePositiveAutoResolved, resolution);
    }

    [Fact]
    public async Task AStaleRegistration_WhoseSiblingIsAlsoBehind_StaysOutstanding()
    {
        var winget = Winget();
        var failure = WingetFailure(OldVcRedist, "14.52.37029.0");
        Path(OldVcRedist, "14.52.37029.0");

        var resolution = await Reconcile(failure,
            winget,
            Under(winget, OldVcRedist, "14.28.29914.0", "Microsoft.VCRedist.2015+.x64"),
            Under(winget, NewVcRedist, "14.44.35211.0", "Microsoft.VCRedist.2015+.x64"));

        Assert.Equal(PatchFailureResolution.Outstanding, resolution);
    }

    [Fact]
    public async Task ACurrentRowWithTheSameIdUnderAnotherManager_IsNoProof()
    {
        var winget = Winget();
        var chocolatey = new InstalledApplication(_hostId, "chocolatey", "2.4.0", "chocolatey");
        var failure = WingetFailure(OldVcRedist, "14.52.37029.0");
        Path(OldVcRedist, "14.52.37029.0");

        var resolution = await Reconcile(failure,
            winget,
            chocolatey,
            Under(winget, OldVcRedist, "14.28.29914.0", "Microsoft.VCRedist.2015+.x64"),
            Under(chocolatey, NewVcRedist, "14.52.37029.0", "Microsoft.VCRedist.2015+.x64"));

        Assert.Equal(PatchFailureResolution.Outstanding, resolution);
    }

    /// The PDFCreator case: winget could not match the install to a package, so its id is the registry key,
    /// which the agent refuses to pass to a script. Nothing will ever retry it.
    [Fact]
    public async Task AnInstallWhoseIdTheAgentRefuses_IsNoLongerApplicable()
    {
        var winget = Winget();
        var failure = WingetFailure("PDFCreator", "6.3.1");
        Path("PDFCreator", "6.3.1");

        var resolution = await Reconcile(failure,
            winget,
            Under(winget, "PDFCreator", "5.0.3", @"ARP\Machine\X64\{8960FCDD-66D4-4180-A172-1AD0DD1A075A}"));

        Assert.Equal(PatchFailureResolution.NoLongerApplicable, resolution);
    }

    /// The AWS CLI "(User)" case: the agent stopped reporting an id for a per-user MSI, so it is never patched.
    [Fact]
    public async Task AnInstallTheAgentNoLongerGivesAnId_IsNoLongerApplicable()
    {
        var winget = Winget();
        var failure = WingetFailure("AWS Command Line Interface v2 (User)", "2.41.1");
        Path("AWS Command Line Interface v2 (User)", "2.42.2");

        var resolution = await Reconcile(failure,
            winget,
            Under(winget, "AWS Command Line Interface v2 (User)", "2.36.10.0", null));

        Assert.Equal(PatchFailureResolution.NoLongerApplicable, resolution);
    }

    [Fact]
    public async Task AnApplicationGoneFromAWingetListingThatWorked_IsNoLongerApplicable()
    {
        var winget = Winget();
        var failure = WingetFailure("AWS Command Line Interface v2 (User)", "2.41.1", failedDaysAgo: 8);

        var resolution = await Reconcile(failure,
            winget,
            Under(winget, "AWS Command Line Interface v2", "2.42.2", "Amazon.AWSCLI"));

        Assert.Equal(PatchFailureResolution.NoLongerApplicable, resolution);
    }

    /// An upgrade that removed the old version and then failed to install the new one leaves the app absent too.
    /// That host has lost the app, so its failure stays visible until the grace period has passed.
    [Fact]
    public async Task AbsentButFailedYesterday_StaysOutstanding()
    {
        var winget = Winget();
        var failure = WingetFailure("LibreOffice 7.6.7.2", "26.8.3.1", failedDaysAgo: 1);

        var resolution = await Reconcile(failure,
            winget,
            Under(winget, "Mozilla Firefox", "133.0", "Mozilla.Firefox"));

        Assert.Equal(PatchFailureResolution.Outstanding, resolution);
    }

    /// `winget list` failed on this check-in: winget's own row is there and nothing under it. An absence then
    /// proves nothing, or one bad listing would close every winget failure the host has.
    [Fact]
    public async Task AnApplicationMissingFromAnEmptyWingetListing_StaysOutstanding()
    {
        var failure = WingetFailure("LibreOffice 7.6.7.2", "26.8.3.1");

        var resolution = await Reconcile(failure, Winget(), new InstalledApplication(_hostId, "Google Chrome", "131.0"));

        Assert.Equal(PatchFailureResolution.Outstanding, resolution);
    }

    [Fact]
    public async Task AnOperatingSystemFailure_IsNeverClosedByItsAbsence()
    {
        var failure = PatchFailure.Open(_hostId, "Windows", "Windows", "26100.1", null, "0x80070306", Now.AddDays(-1));

        var resolution = await Reconcile(failure, new InstalledApplication(_hostId, "Google Chrome", "131.0"));

        Assert.Equal(PatchFailureResolution.Outstanding, resolution);
    }

    /// A failed install leaves nothing installed by definition; that is the failure, not a reason to close it.
    [Fact]
    public async Task AFailedInstall_IsNeverClosedByTheApplicationBeingAbsent()
    {
        var winget = Winget();
        var failure = WingetFailure("Slack", "installed");

        var resolution = await Reconcile(failure, winget, Under(winget, "Zoom", "6.2.0", "Zoom.Zoom"));

        Assert.Equal(PatchFailureResolution.Outstanding, resolution);
    }

    [Theory]
    [InlineData("Mozilla.Firefox", true)]
    [InlineData("9NKSQGP7F2NH", true)]
    [InlineData(@"ARP\Machine\X64\{8960FCDD-66D4-4180-A172-1AD0DD1A075A}", false)]
    [InlineData(@"MSIX\Microsoft.Paint_11.2410.28.0_x64__8wekyb3d8bbwe", false)]
    [InlineData("hashicorp/tap/terraform", false)]
    [InlineData("..", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("bad\nid", false)]
    public void IsUsableIdentifier_MirrorsWhatTheAgentsAccept(string? identifier, bool usable) =>
        Assert.Equal(usable, InstallationRules.IsUsableIdentifier(identifier));
}
