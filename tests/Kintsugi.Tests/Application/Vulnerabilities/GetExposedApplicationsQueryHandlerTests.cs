using Moq;
using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Application.UpgradePaths;
using Kintsugi.Application.Vulnerabilities;
using Kintsugi.Application.Vulnerabilities.Queries.GetExposedApplications;
using Kintsugi.Domain.Entities;
using Kintsugi.Domain.Enums;

namespace Kintsugi.Tests.Application.Vulnerabilities;

/// <summary>
/// The "Exposed, not patched by Kintsugi" list: an application with an open CVE that Kintsugi's own patching will not
/// fix. What these pin is that marking an application as managed elsewhere never takes it off the list, and that
/// an application Kintsugi does patch never gets on it.
/// </summary>
public class GetExposedApplicationsQueryHandlerTests
{
    private readonly Mock<IVulnerabilityRepository> _vulnerabilities = new();
    private readonly Mock<IManagedApplicationRepository> _managed = new();
    private readonly Mock<IUpgradePathRepository> _upgradePaths = new();
    private readonly List<ApplicationFindingRow> _findings = new();
    private readonly List<InstallationCount> _installed = new();
    private readonly List<ManagedApplication> _managedRows = new();
    private readonly List<UpgradePathStatusRow> _paths = new();

    public GetExposedApplicationsQueryHandlerTests()
    {
        _vulnerabilities.Setup(v => v.GetApplicationFindingsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(_findings);
        _vulnerabilities.Setup(v => v.GetApplicationInstallationCountsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(_installed);
        _managed.Setup(m => m.GetAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync(_managedRows);
        _upgradePaths.Setup(u => u.GetPathStatusesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(_paths);
    }

    private static IEnumerable<ApplicationFindingRow> Finding(string cve, double? score, bool exploited, params (string Name, string Version)[] subjects) =>
        subjects.Select(x => new ApplicationFindingRow(cve, score, exploited, false, x.Name.ToLowerInvariant(), x.Name, x.Version));

    private static UpgradePathStatusRow Path(string name, UpgradePathStatus status) => new(name, "Windows", status);

    private Task<ExposedApplicationsDto> Run() =>
        new GetExposedApplicationsQueryHandler(_vulnerabilities.Object, _managed.Object, _upgradePaths.Object)
            .Handle(new GetExposedApplicationsQuery(), CancellationToken.None);

    [Fact]
    public async Task AManagedApplicationWithACve_IsListedWithItsOwner_AndWhoDecided()
    {
        _findings.AddRange(Finding("CVE-1", 8.1, false, ("Ninja Remote", "5.0")));
        _installed.Add(new InstallationCount("ninja remote", "5.0", 12));
        _managedRows.Add(ManagedApplication.Create("Ninja*", "NinjaOne", ManagedSource.Auto));

        var row = Assert.Single((await Run()).Applications);

        Assert.Equal(("Ninja Remote", ExposureRoute.ManagedElsewhere, "NinjaOne", ManagedSource.Auto), (row.ApplicationName, row.Route, row.Owner, row.Source));
        Assert.Equal((ExposureCriticality.High, 12, 1), (row.Criticality, row.HostCount, row.CveCount));
    }

    [Fact]
    public async Task AnApplicationWithNoOwnerAndNoUpgradePath_IsListed_AsTheWorstState()
    {
        _findings.AddRange(Finding("CVE-1", 5.0, false, ("NextVoice", "1.0")));
        _paths.Add(Path("NextVoice", UpgradePathStatus.NotFound));

        var row = Assert.Single((await Run()).Applications);

        Assert.Equal((ExposureRoute.NoUpgradePath, null, null), (row.Route, row.Owner, row.Source));
    }

    [Fact]
    public async Task AFailedCheckCountsAsNoPath_ButAnyFoundPathKeepsItOffTheList()
    {
        _findings.AddRange(Finding("CVE-1", 9.8, false, ("Failed App", "1")));
        _findings.AddRange(Finding("CVE-2", 9.8, false, ("Patched App", "1")));
        _paths.Add(Path("Failed App", UpgradePathStatus.Failed));
        _paths.Add(Path("Patched App", UpgradePathStatus.Found));
        _paths.Add(Path("Patched App", UpgradePathStatus.NotFound));

        Assert.Equal(new[] { "Failed App" }, (await Run()).Applications.Select(a => a.ApplicationName));
    }

    [Fact]
    public async Task AnApplicationNotResearchedYet_IsLeftToTheNormalPipeline()
    {
        _findings.AddRange(Finding("CVE-1", 9.8, false, ("Brand New", "1")));

        Assert.Empty((await Run()).Applications);
    }

    [Fact]
    public async Task ManagingAnApplication_DoesNotHideItsCves()
    {
        _findings.AddRange(Finding("CVE-1", 9.8, true, ("Self Service+", "2.0")));
        _managedRows.Add(ManagedApplication.Create("Self Service+", "Jamf Pro"));

        var row = Assert.Single((await Run()).Applications);

        Assert.Equal((ExposureCriticality.Critical, true), (row.Criticality, row.KnownExploited));
    }

    [Theory]
    [InlineData(false, null, ExposureCriticality.Unscored)]
    [InlineData(true, 2.0, ExposureCriticality.Critical)]
    [InlineData(false, 9.0, ExposureCriticality.Critical)]
    [InlineData(false, 8.9, ExposureCriticality.High)]
    [InlineData(false, 7.0, ExposureCriticality.High)]
    [InlineData(false, 6.9, ExposureCriticality.Medium)]
    [InlineData(false, 4.0, ExposureCriticality.Medium)]
    [InlineData(false, 3.9, ExposureCriticality.Low)]
    public void Criticality_IsExploitedFirst_ThenTheWorstScore(bool exploited, double? score, ExposureCriticality expected) =>
        Assert.Equal(expected, ExposureRules.Criticality(exploited, score));

    [Fact]
    public async Task OneApplicationAcrossManyCves_IsOneRow_WithTheWorstCveToQuote()
    {
        _findings.AddRange(Finding("CVE-LOW", 3.0, false, ("Python 3.12.10", "3.12.10")));
        _findings.AddRange(Finding("CVE-EXPLOITED", 5.0, true, ("Python 3.12.10", "3.12.10")));
        _findings.AddRange(Finding("CVE-HIGH", 9.0, false, ("Python 3.12.10", "3.12.10")));
        _installed.Add(new InstallationCount("python 3.12.10", "3.12.10", 4));
        _paths.Add(Path("Python 3.12.10", UpgradePathStatus.NotFound));

        var row = Assert.Single((await Run()).Applications);

        Assert.Equal((3, "CVE-EXPLOITED", 9.0, 4), (row.CveCount, row.WorstCveId, row.WorstCvssScore, row.HostCount));
    }

    [Fact]
    public async Task TheListIsMostUrgentFirst_ThenNoRouteBeforeAnOwner_ThenMostHosts()
    {
        _findings.AddRange(Finding("CVE-1", 5.0, false, ("Medium Owned", "1")));
        _findings.AddRange(Finding("CVE-2", 5.0, false, ("Medium Orphan", "1")));
        _findings.AddRange(Finding("CVE-3", 9.8, false, ("Critical Owned", "1")));
        _paths.Add(Path("Medium Orphan", UpgradePathStatus.NotFound));
        _managedRows.Add(ManagedApplication.Create("Medium Owned", "Vendor"));
        _managedRows.Add(ManagedApplication.Create("Critical Owned", "Vendor"));

        Assert.Equal(
            new[] { "Critical Owned", "Medium Orphan", "Medium Owned" },
            (await Run()).Applications.Select(a => a.ApplicationName));
    }
}
