using Microsoft.EntityFrameworkCore;
using Kintsugi.Domain.Entities;
using Kintsugi.Infrastructure.Persistence;
using Kintsugi.Infrastructure.Persistence.Repositories;

namespace Kintsugi.Tests.Infrastructure;

public class ManagedApplicationRepositoryTests
{
    private static ApplicationDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new ApplicationDbContext(options);
    }

    [Fact]
    public async Task FindMatchingAsync_FindsAnApplicationCoveredOnlyByAPattern()
    {
        await using var context = CreateContext();
        context.ManagedApplications.Add(ManagedApplication.Create("CrowdStrike*", "CrowdStrike"));
        context.ManagedApplications.Add(ManagedApplication.Create("Zoom", "Jamf Pro"));
        await context.SaveChangesAsync();
        var repository = new ManagedApplicationRepository(context);

        Assert.Equal("CrowdStrike*", (await repository.FindMatchingAsync("CrowdStrike Windows Sensor", CancellationToken.None))!.ApplicationName);
        Assert.Equal("Zoom", (await repository.FindMatchingAsync(" zoom ", CancellationToken.None))!.ApplicationName);
        Assert.Null(await repository.FindMatchingAsync("Slack", CancellationToken.None));
        // FindAsync is the exact row only, which is why callers asking "is it managed?" must not use it.
        Assert.Null(await repository.FindAsync("CrowdStrike Windows Sensor", CancellationToken.None));
    }

    [Fact]
    public async Task FindAsync_FindsAnApplicationWhateverCaseItIsAskedForIn()
    {
        await using var context = CreateContext();
        context.ManagedApplications.Add(ManagedApplication.Create("Falcon Sensor", "CrowdStrike"));
        await context.SaveChangesAsync();
        var repository = new ManagedApplicationRepository(context);

        var found = await repository.FindAsync("  FALCON sensor ", CancellationToken.None);

        Assert.Equal("Falcon Sensor", found!.ApplicationName);
        Assert.Null(await repository.FindAsync("Slack", CancellationToken.None));
    }

    [Fact]
    public async Task GetOwnersByNameAsync_IsLookedUpWithoutRegardToCase()
    {
        await using var context = CreateContext();
        context.ManagedApplications.Add(ManagedApplication.Create("Falcon Sensor", "CrowdStrike"));
        await context.SaveChangesAsync();
        var repository = new ManagedApplicationRepository(context);

        var owners = await repository.GetOwnersByNameAsync(CancellationToken.None);

        Assert.Equal("CrowdStrike", owners["FALCON SENSOR"]);
        Assert.False(owners.ContainsKey("Slack"));
    }

    [Fact]
    public async Task OwnerIsInUseAsync_IsTrueOnlyWhileAnApplicationIsManagedByIt()
    {
        await using var context = CreateContext();
        context.ManagedApplications.Add(ManagedApplication.Create("Falcon Sensor", "CrowdStrike"));
        await context.SaveChangesAsync();
        var repository = new ManagedApplicationRepository(context);

        Assert.True(await repository.OwnerIsInUseAsync("crowdstrike", CancellationToken.None));
        Assert.False(await repository.OwnerIsInUseAsync("NinjaOne", CancellationToken.None));
    }

    [Fact]
    public async Task FindOwnerAsync_FindsAToolWhateverItsCase_AndGetOwnersAsyncListsThemByName()
    {
        await using var context = CreateContext();
        context.ManagedOwners.AddRange(ManagedOwner.Create("NinjaOne"), ManagedOwner.Create("CrowdStrike"));
        await context.SaveChangesAsync();
        var repository = new ManagedApplicationRepository(context);

        Assert.Equal("NinjaOne", (await repository.FindOwnerAsync("ninjaone", CancellationToken.None))!.Name);
        Assert.Equal(new[] { "CrowdStrike", "NinjaOne" }, (await repository.GetOwnersAsync(CancellationToken.None)).Select(o => o.Name));
    }

    [Fact]
    public void ManagedApplication_IsRefusedWithoutANameOrAnOwner_AndTooLongOnes()
    {
        Assert.ThrowsAny<Exception>(() => ManagedApplication.Create("  ", "NinjaOne"));
        Assert.ThrowsAny<Exception>(() => ManagedApplication.Create("Falcon", ""));
        Assert.ThrowsAny<Exception>(() => ManagedApplication.Create(new string('x', 256), "NinjaOne"));
        Assert.ThrowsAny<Exception>(() => ManagedApplication.Create("Falcon", new string('x', 65)));
    }
}
