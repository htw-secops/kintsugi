using Moq;
using Kintsugi.Application.Common.Exceptions;
using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Application.HostActivity;
using Kintsugi.Application.Hosts.Commands.SetHostMaintenance;
using Kintsugi.Domain.Entities;

namespace Kintsugi.Tests.Application.Hosts;

public class SetHostMaintenanceCommandHandlerTests
{
    private readonly Mock<IHostRepository> _hosts = new();
    private readonly Mock<IHostEventRecorder> _events = new();
    private readonly Mock<IUnitOfWork> _unitOfWork = new();
    private readonly Host _host = new("pc-1", "SERIAL-1", "Windows 11");

    public SetHostMaintenanceCommandHandlerTests()
    {
        _hosts.Setup(r => r.GetByIdAsync(_host.Id, It.IsAny<CancellationToken>())).ReturnsAsync(_host);
    }

    private SetHostMaintenanceCommandHandler CreateHandler() => new(_hosts.Object, _events.Object, _unitOfWork.Object);

    private SetHostMaintenanceCommand Command(bool on, DateTimeOffset? until = null, string? reason = null) =>
        new(_host.Id, on, until, reason, "admin@example.com");

    [Fact]
    public async Task Start_PutsTheHostInMaintenance_RecordsWhoDidIt_AndSaves()
    {
        var until = DateTimeOffset.UtcNow.AddHours(2);

        await CreateHandler().Handle(Command(on: true, until, "replacing the disk"), CancellationToken.None);

        Assert.True(_host.IsInMaintenance(DateTimeOffset.UtcNow));
        _events.Verify(e => e.HostProfileChangedAsync(
            _host.Id, "admin@example.com", It.Is<string>(d => d.Contains("maintenance mode started") && d.Contains("replacing the disk")),
            It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()), Times.Once);
        _unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task End_TakesTheHostOutOfMaintenance_AndRecordsIt()
    {
        _host.StartMaintenance(DateTimeOffset.UtcNow, until: null, reason: null);

        await CreateHandler().Handle(Command(on: false), CancellationToken.None);

        Assert.False(_host.IsInMaintenance(DateTimeOffset.UtcNow));
        _events.Verify(e => e.HostProfileChangedAsync(
            _host.Id, "admin@example.com", "maintenance mode ended", It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()), Times.Once);
        _unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task End_OnAHostNotInMaintenance_ChangesNothingAndLeavesNoEvent()
    {
        await CreateHandler().Handle(Command(on: false), CancellationToken.None);

        _events.Verify(e => e.HostProfileChangedAsync(
            It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()), Times.Never);
        _unitOfWork.Verify(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task AnUnknownHost_IsNotFound()
    {
        var command = new SetHostMaintenanceCommand(Guid.NewGuid(), true, null, null, "admin@example.com");

        await Assert.ThrowsAsync<NotFoundException>(() => CreateHandler().Handle(command, CancellationToken.None));
    }

    private readonly SetHostMaintenanceCommandValidator _validator = new();

    [Fact]
    public void Validator_AcceptsAnOpenEndedStart_AndAFutureEnd()
    {
        Assert.True(_validator.Validate(Command(on: true)).IsValid);
        Assert.True(_validator.Validate(Command(on: true, DateTimeOffset.UtcNow.AddHours(1))).IsValid);
    }

    [Fact]
    public void Validator_RefusesAnEndInThePast_WhenStarting()
    {
        var result = _validator.Validate(Command(on: true, DateTimeOffset.UtcNow.AddMinutes(-1)));

        Assert.False(result.IsValid);
        Assert.Contains("must be in the future", result.Errors.Single().ErrorMessage);
    }

    [Fact]
    public void Validator_IgnoresTheEndTime_WhenEnding()
    {
        Assert.True(_validator.Validate(Command(on: false, DateTimeOffset.UtcNow.AddMinutes(-1))).IsValid);
    }

    [Fact]
    public void Validator_RefusesAMissingActor_AndATooLongReason()
    {
        Assert.False(_validator.Validate(Command(on: true) with { Actor = "" }).IsValid);
        Assert.False(_validator.Validate(Command(on: true, reason: new string('x', Host.MaxMaintenanceReasonLength + 1))).IsValid);
        Assert.True(_validator.Validate(Command(on: true, reason: new string('x', Host.MaxMaintenanceReasonLength))).IsValid);
    }
}
