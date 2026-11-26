using MediatR;
using Moq;
using Kintsugi.Application.Applications;
using Kintsugi.Application.Applications.Queries.GetApplicationOverview;
using Kintsugi.Application.Applications.Queries.GetApplicationSummaries;
using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Application.UpgradePaths;
using Kintsugi.Application.UpgradePaths.Queries.GetUpgradePathSummaries;
using Kintsugi.Domain.Entities;
using Kintsugi.Domain.Enums;

namespace Kintsugi.Tests.Application.Applications;

public class GetApplicationOverviewQueryHandlerTests
{
    private static ApplicationSummaryDto Summary(string name) =>
        new(name, 1, new[] { "host-1" }, Array.Empty<ApplicationSummaryDto>());

    [Fact]
    public async Task EachRowSaysWhoPatchesIt_AndWhoDecided_FromAnExactNameOrAPattern()
    {
        var sender = new Mock<ISender>();
        sender.Setup(s => s.Send(It.IsAny<GetApplicationSummariesQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { Summary("Falcon"), Summary("Microsoft Teams Meeting Add-in for Microsoft Office"), Summary("Slack") });
        sender.Setup(s => s.Send(It.IsAny<GetUpgradePathSummariesQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<UpgradePathSummaryDto>());
        var managed = new Mock<IManagedApplicationRepository>();
        managed.Setup(m => m.GetAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new[]
        {
            ManagedApplication.Create("Falcon", "CrowdStrike"),
            ManagedApplication.Create("Microsoft Teams Meeting Add-in*", "Microsoft Teams", ManagedSource.Auto),
        });

        var result = await new GetApplicationOverviewQueryHandler(sender.Object, managed.Object)
            .Handle(new GetApplicationOverviewQuery(), CancellationToken.None);

        var byName = result.Applications.ToDictionary(a => a.Name);
        Assert.Equal(("CrowdStrike", ManagedSource.Admin), (byName["Falcon"].ManagedBy, byName["Falcon"].ManagedSource));
        Assert.Equal(("Microsoft Teams", ManagedSource.Auto),
            (byName["Microsoft Teams Meeting Add-in for Microsoft Office"].ManagedBy, byName["Microsoft Teams Meeting Add-in for Microsoft Office"].ManagedSource));
        Assert.Null(byName["Slack"].ManagedBy);
        Assert.Null(byName["Slack"].ManagedSource);
    }
}
