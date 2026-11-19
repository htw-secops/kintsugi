using Microsoft.EntityFrameworkCore;
using Kintsugi.Domain.Entities;
using Kintsugi.Domain.Enums;
using Kintsugi.Infrastructure.Persistence;
using Kintsugi.Infrastructure.Persistence.Repositories;

namespace Kintsugi.Tests.Infrastructure;

public class PatchFailureRepositoryTests
{
    [Fact]
    public async Task CountOutstandingByHost_CountsOnlyOutstandingRows_PerHost()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        await using var context = new ApplicationDbContext(options);
        var twice = Guid.NewGuid();
        var resolved = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        var dismissed = PatchFailure.Open(resolved, "chrome", "Windows", "1", "2", "failed", now);
        dismissed.Resolve(PatchFailureResolution.Dismissed);
        context.PatchFailures.AddRange(
            PatchFailure.Open(twice, "chrome", "Windows", "1", "2", "failed", now),
            PatchFailure.Open(twice, "zoom", "Windows", "1", "2", "failed", now),
            dismissed);
        await context.SaveChangesAsync();

        var counts = await new PatchFailureRepository(context).CountOutstandingByHostAsync(CancellationToken.None);

        Assert.Equal(2, counts[twice]);
        Assert.False(counts.ContainsKey(resolved));
    }
}
