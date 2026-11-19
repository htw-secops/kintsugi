using MediatR;

namespace Kintsugi.Application.Hosts.Commands.RevealAssignedPerson;

public record AssignedPersonDto(string? Name, string? Email);

/// <summary>
/// Reads the full name and email of the person a host is assigned to. **A command, not a query, on purpose:** reading
/// them writes an <c>AssignedPersonRevealed</c> event with the administrator's name, because they are the only personal
/// details Kintsugi keeps and a read nobody could see afterwards would not be a control.
/// </summary>
/// <param name="Actor">The signed-in administrator, set by the controller and never read from the body.</param>
public record RevealAssignedPersonCommand(Guid HostId, string Actor) : IRequest<AssignedPersonDto>;
