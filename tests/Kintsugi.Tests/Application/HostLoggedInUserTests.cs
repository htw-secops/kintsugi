using Moq;
using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Application.Hosts;
using Kintsugi.Application.Hosts.Commands.CreateHost;
using Kintsugi.Domain.Entities;

namespace Kintsugi.Tests.Application;

public class HostLoggedInUserTests
{
    [Fact]
    public void AReportedUser_IsRecordedAndCarriedOnTheDto()
    {
        var host = new Host("h", "S1");

        host.RecordLoggedInUser("shane.hughes");

        Assert.Equal("shane.hughes", HostDto.FromEntity(host).LastLoggedInUser);
    }

    /// <summary>A laptop at the login window must stay findable by its owner.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NobodyLoggedOn_KeepsTheLastUser(string? reported)
    {
        var host = new Host("h", "S1");
        host.RecordLoggedInUser("alice");

        host.RecordLoggedInUser(reported);

        Assert.Equal("alice", host.LastLoggedInUser);
    }

    /// <summary>A Linux server with many SSH sessions must not fail its check-in over a display string.</summary>
    [Fact]
    public void ALongList_IsCutToTheColumnAndStillValid()
    {
        var tooLong = string.Join(", ", Enumerable.Range(0, 100).Select(i => $"user{i}"));
        var host = new Host("h", "S1");

        host.RecordLoggedInUser(tooLong);

        Assert.Equal(Host.MaxLastLoggedInUserLength, host.LastLoggedInUser!.Length);
        Assert.True(new CreateHostCommandValidator().Validate(new CreateHostCommand("h", "S1", LoggedInUser: tooLong)).IsValid);
    }

    [Fact]
    public async Task TheCheckIn_RecordsItForANewHostAndAnExistingOne()
    {
        Host? added = null;
        var hosts = new Mock<IHostRepository>();
        hosts.Setup(r => r.AddAsync(It.IsAny<Host>(), It.IsAny<CancellationToken>()))
            .Callback<Host, CancellationToken>((h, _) => added = h);
        var handler = new CreateHostCommandHandler(hosts.Object, Mock.Of<IUnitOfWork>(), Mock.Of<ICheckInLoadBalancer>());

        var created = await handler.Handle(new CreateHostCommand("h", "S1", LoggedInUser: "alice"), CancellationToken.None);
        hosts.Setup(r => r.GetBySerialNumberAsync("S1", It.IsAny<CancellationToken>())).ReturnsAsync(added);
        var again = await handler.Handle(new CreateHostCommand("h", "S1", LoggedInUser: "bob"), CancellationToken.None);

        Assert.Equal("alice", created.Host.LastLoggedInUser);
        Assert.Equal("bob", again.Host.LastLoggedInUser);
    }
}
