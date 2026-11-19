using MediatR;
using Kintsugi.Application.ForcedPatchRuns;

namespace Kintsugi.Application.SoftwareTitles.Commands.RunSoftwareTitleNow;

/// <summary>
/// "Run now": asks every host the title still has work on to act at once rather than at its next
/// hourly check-in. Rides forced patch runs (<c>RequestForcedPatchRunsCommand</c>) by the title's name —
/// a forced run carries a name and nothing else, and the agent resolves it through its ordinary work
/// list, so the action row it runs is verified exactly as at check-in.
/// </summary>
/// <param name="RequestedBy">The signed-in administrator, from the session; never from the body.</param>
public record RunSoftwareTitleNowCommand(Guid Id, string? RequestedBy = null) : IRequest<RequestForcedPatchRunsResult>;
