using MediatR;
using Kintsugi.Application.Common.Exceptions;
using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Application.HostActivity;

namespace Kintsugi.Application.Hosts.Commands.RevealAssignedPerson;

public class RevealAssignedPersonCommandHandler : IRequestHandler<RevealAssignedPersonCommand, AssignedPersonDto>
{
    private readonly IHostRepository _hosts;
    private readonly IHostEventRecorder _events;
    private readonly IUnitOfWork _unitOfWork;

    public RevealAssignedPersonCommandHandler(IHostRepository hosts, IHostEventRecorder events, IUnitOfWork unitOfWork)
    {
        _hosts = hosts;
        _events = events;
        _unitOfWork = unitOfWork;
    }

    public async Task<AssignedPersonDto> Handle(RevealAssignedPersonCommand request, CancellationToken cancellationToken)
    {
        var host = await _hosts.GetByIdAsync(request.HostId, cancellationToken)
            ?? throw new NotFoundException($"No host is registered with id '{request.HostId}'.");

        // Nothing to reveal means nothing was read, so nothing is recorded: the timeline lists reveals, not button
        // presses on a host with no person on it.
        if (host.AssignedName is null && host.AssignedEmail is null)
        {
            return new AssignedPersonDto(null, null);
        }

        await _events.AssignedPersonRevealedAsync(host.Id, request.Actor, DateTimeOffset.UtcNow, cancellationToken);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return new AssignedPersonDto(host.AssignedName, host.AssignedEmail);
    }
}
