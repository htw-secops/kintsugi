using MediatR;

namespace Kintsugi.Application.SoftwareTitles.Commands.AssignSoftwareTitle;

/// <summary>
/// Sets who a title applies to: every host of its OS, a list of hosts by name, or both. Replaces the
/// previous assignment; an empty one stops the title acting anywhere without deleting it.
/// </summary>
/// <param name="AssignedBy">The signed-in administrator, from the session; never from the body.</param>
public record AssignSoftwareTitleCommand(Guid Id, bool AllHosts, IReadOnlyList<string> HostNames, string? AssignedBy = null)
    : IRequest<SoftwareScreenDto>;
