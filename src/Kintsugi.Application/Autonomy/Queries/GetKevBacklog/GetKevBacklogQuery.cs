using MediatR;

namespace Kintsugi.Application.Autonomy.Queries.GetKevBacklog;

/// <param name="Days">How many days of history, 7 to 365; 60 when outside that.</param>
public record GetKevBacklogQuery(int Days = 60) : IRequest<KevBacklogDto>;

/// <summary>One day. <paramref name="DateUtc"/> is midnight UTC.</summary>
public record KevBacklogPointDto(
    DateTimeOffset DateUtc,
    int DevicesOpen,
    int WithinTarget,
    int Overdue,
    int DueWithin7Days,
    int OverdueNotSeen7Days,
    string? TopCveId,
    int TopCveDevices);

/// <param name="Series">The recorded days before today, oldest first. Empty on the day it is first recorded: nothing is back-filled.</param>
/// <param name="Today">Worked out now, so the chart has a last point before today's record exists.</param>
/// <param name="Target">What "overdue" means, for the chart's footnote.</param>
public record KevBacklogDto(IReadOnlyList<KevBacklogPointDto> Series, KevBacklogPointDto Today, string Target);
