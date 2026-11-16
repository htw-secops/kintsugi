using MediatR;
using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Application.HostActivity;

namespace Kintsugi.Application.Hosts.Commands.UpdateHostProfile;

public class UpdateHostProfileCommandHandler : IRequestHandler<UpdateHostProfileCommand, int>
{
    private readonly IHostRepository _hosts;
    private readonly IHostEventRecorder _events;
    private readonly IUnitOfWork _unitOfWork;

    public UpdateHostProfileCommandHandler(IHostRepository hosts, IHostEventRecorder events, IUnitOfWork unitOfWork)
    {
        _hosts = hosts;
        _events = events;
        _unitOfWork = unitOfWork;
    }

    /// <returns>How many hosts actually changed. A host that already had the value counts for nothing and leaves no
    /// event, so repeating a bulk edit is harmless.</returns>
    public async Task<int> Handle(UpdateHostProfileCommand request, CancellationToken cancellationToken)
    {
        var hosts = await _hosts.GetByIdsAsync(request.HostIds.Distinct().ToList(), cancellationToken);
        var now = DateTimeOffset.UtcNow;
        var updated = 0;

        foreach (var host in hosts)
        {
            var clear = request.ClearAssignedPerson ? string.Empty : null;   // empty clears, null keeps (Host.SetProfile)
            var changed = host.SetProfile(request.Organization, request.Department, request.Location, assignedName: clear, assignedEmail: clear);
            if (changed.Count == 0)
            {
                continue;
            }

            updated++;
            // The fields, never their values: the timeline says what kind of thing changed and who changed it.
            await _events.HostProfileChangedAsync(host.Id, request.Actor, string.Join(", ", changed), now, cancellationToken);
        }

        if (updated > 0)
        {
            await _unitOfWork.SaveChangesAsync(cancellationToken);
        }

        return updated;
    }
}
