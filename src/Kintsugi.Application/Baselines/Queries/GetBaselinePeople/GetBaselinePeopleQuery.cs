using MediatR;
using Kintsugi.Domain.Enums;

namespace Kintsugi.Application.Baselines.Queries.GetBaselinePeople;

/// <summary>
/// The signatures drill-down (<c>GET /api/admin/baselines/{id}/people</c>): every signed-in person the
/// baseline's hosts reported, who the server resolved them to and why, and their state against each
/// per-person item of the served version. Read by the admin UI's <c>web/lib/data/models/</c> mapper (part 3
/// of IP-871), which mirrors it by hand.
/// </summary>
public record GetBaselinePeopleQuery(Guid BaselineId) : IRequest<IReadOnlyList<BaselinePersonRowDto>>;

/// <summary>One (host, Windows user, per-person item). <paramref name="State"/> and what follows are null until the
/// person has reported, and always for someone nobody was resolved to.</summary>
/// <param name="State">An ordinal on this wire, as everywhere else the admin UI reads the enum.</param>
/// <param name="LastSeenUtc">When the agent last listed this session.</param>
public record BaselinePersonRowDto(
    Guid HostId,
    string Hostname,
    string WindowsUser,
    string? Email,
    string? ResolvedBy,
    string? Reason,
    DateTimeOffset? CopyRenderedAtUtc,
    string ItemKey,
    BaselineItemState? State,
    string? Observed,
    string? Detail,
    DateTimeOffset? LastCheckedUtc,
    DateTimeOffset LastSeenUtc);
