using MediatR;
using Kintsugi.Application.Common.Interfaces;

namespace Kintsugi.Application.Baselines.Commands.ResolveBaselinePeople;

/// <summary>
/// The Windows agent's per-person half of a baseline check (<c>POST /api/baseline-people</c>): the sessions
/// signed in now, answered with who each one is, their signature copy and the states the server holds for
/// them. Mirrored by hand by the Windows agent's <c>baseline.rs</c> (part 4 of IP-871) — see
/// <c>.claude/rules/hand-mirrored-dtos.md</c>.
/// </summary>
/// <remarks>
/// A command, not a query: every call records what the server made of each session
/// (<c>HostPersonResolution</c>), which is what later lets a per-person result in
/// <c>POST /api/baseline-results</c> be trusted. <see cref="IAgentScopedRequest"/> ties the serial number to
/// the client certificate, so an agent can only ever ask about, and be served copies for, its own sessions.
/// </remarks>
public record ResolveBaselinePeopleCommand(string SerialNumber, IReadOnlyList<SessionInput> Sessions)
    : IRequest<BaselinePeopleDto>, IAgentScopedRequest;

/// <param name="WindowsUser">The session's Windows username, as the agent read it.</param>
/// <param name="GcpwEmail">The Google account the Google Credential Provider recorded for that user's SID,
/// if any. A hint the server checks against its copies, never an identity it takes on trust.</param>
public record SessionInput(string WindowsUser, string? GcpwEmail);

/// <summary>What <c>POST /api/baseline-people</c> answers. Empty when no baseline served to the host holds a
/// per-person item.</summary>
public record BaselinePeopleDto(IReadOnlyList<BaselinePersonDto> People);

/// <param name="Email">The person resolved, lower-cased; null with a <paramref name="Reason"/> when nobody was.</param>
/// <param name="ResolvedBy"><c>gcpw</c>, <c>username</c>, or null.</param>
/// <param name="Copy">The person's signature, only when <paramref name="Email"/> is set.</param>
/// <param name="States">What the server holds for this person against each per-person item it serves this host,
/// so the agent sends only what changed.</param>
public record BaselinePersonDto(
    string WindowsUser,
    string? Email,
    string? ResolvedBy,
    string? Reason,
    PersonSignatureCopyDto? Copy,
    IReadOnlyList<PersonItemStateDto> States);

/// <param name="Sha256">Lower-case hex SHA-256 of <paramref name="Html"/>'s UTF-8 bytes, computed by this server.</param>
/// <param name="Reply">False: the person turned the signature off for replies, so the agent removes ours from
/// the reply defaults. Mirrored as <c>SignatureCopy.reply</c> in the Windows agent, which defaults it to true.</param>
public record PersonSignatureCopyDto(string Html, string Text, string Sha256, DateTimeOffset RenderedAtUtc, bool Reply);

/// <param name="State">A <c>BaselineItemState</c> member name, as on the rest of the agent's wire.</param>
public record PersonItemStateDto(Guid VersionId, string Key, string State, string? Observed);
