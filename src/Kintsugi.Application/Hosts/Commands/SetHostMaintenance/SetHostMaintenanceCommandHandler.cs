using MediatR;
using Kintsugi.Application.Common.Exceptions;
using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Application.HostActivity;
using Kintsugi.Domain.Entities;

namespace Kintsugi.Application.Hosts.Commands.SetHostMaintenance;

public class SetHostMaintenanceCommandHandler : IRequestHandler<SetHostMaintenanceCommand>
{
    private readonly IHostRepository _hosts;
    private readonly IHostEventRecorder _events;
    private readonly IUnitOfWork _unitOfWork;

    public SetHostMaintenanceCommandHandler(IHostRepository hosts, IHostEventRecorder events, IUnitOfWork unitOfWork)
    {
        _hosts = hosts;
        _events = events;
        _unitOfWork = unitOfWork;
    }

    public async Task Handle(SetHostMaintenanceCommand request, CancellationToken cancellationToken)
    {
        var host = await _hosts.GetByIdAsync(request.HostId, cancellationToken)
            ?? throw new NotFoundException($"No host is registered with id '{request.HostId}'.");
        var now = DateTimeOffset.UtcNow;

        if (request.On)
        {
            host.StartMaintenance(now, request.Until, request.Reason);
        }
        else if (!host.EndMaintenance(now))
        {
            return;   // Ending what is not on changes nothing and leaves no event.
        }

        // shortcut: reuses the "Host details" event rather than a new event kind, so the timeline says who did it
        // and when but not with its own icon. Upgrade when the timeline needs to filter on it.
        await _events.HostProfileChangedAsync(
            host.Id, request.Actor, request.On ? Started(host) : "maintenance mode ended", now, cancellationToken);
        await _unitOfWork.SaveChangesAsync(cancellationToken);
    }

    // The hold ends by itself, with no event, so the start event keeps the end time and the reason.
    private static string Started(Host host)
    {
        var until = host.MaintenanceUntilUtc is { } u ? $" until {u.ToString("d MMM HH:mm", System.Globalization.CultureInfo.InvariantCulture)} UTC" : "";
        var reason = string.IsNullOrWhiteSpace(host.MaintenanceReason) ? "" : $": {host.MaintenanceReason}";
        return $"maintenance mode started{until}{reason}";
    }
}
