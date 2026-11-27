using Kintsugi.Domain.Common;

namespace Kintsugi.Domain.Entities;

/// <summary>
/// One investigation Kintsugi ran by itself when a device turned Unstable: it asked the device for its log, wrote
/// down the likely cause and the fix, and told support. What a person used to do from the device report by hand.
/// </summary>
/// <remarks>
/// Waiting while <see cref="ReportedUtc"/> is null; reported after. The report is a hint with its evidence, like the
/// device report it is built from, and says whether the AI wrote it. See "Automatic investigation" in
/// <c>src/CLAUDE.md</c> and <c>RunDeviceInvestigationsCommandHandler</c>.
/// </remarks>
public class DeviceInvestigation : BaseEntity
{
    /// <summary>The name on the log request and the device's timeline: nobody pressed anything.</summary>
    public const string Requester = "Kintsugi (automatic)";

    public const int MaxWhyLength = 1000;
    public const int MaxCauseLength = 300;
    public const int MaxReportLength = 8000;
    public const int MaxAlertErrorLength = 500;

    /// <summary>How many times a failed alert is sent again, one pass apart, before it is left on the page.</summary>
    public const int MaxAlertAttempts = 5;

    public Guid HostId { get; private set; }

    public DateTimeOffset OpenedUtc { get; private set; }

    /// <summary>Why the device was Unstable when this was opened, in the health assessment's words.</summary>
    public string Why { get; private set; } = default!;

    /// <summary>Fatal hardware errors in 30 days at opening: a new one is worse, and opens another investigation.</summary>
    public int FatalErrorsAtOpen { get; private set; }

    /// <summary>The log request this investigation waits for; null when one could not be made.</summary>
    public Guid? LogCollectionId { get; private set; }

    public DateTimeOffset? ReportedUtc { get; private set; }

    public string? LikelyCause { get; private set; }

    public string? Report { get; private set; }

    public bool WrittenByAi { get; private set; }

    public DateTimeOffset? AlertedUtc { get; private set; }

    public string? AlertError { get; private set; }

    public int AlertAttempts { get; private set; }

    private DeviceInvestigation()
    {
    }

    public static DeviceInvestigation Open(Guid hostId, string why, int fatalErrors, Guid? logCollectionId, DateTimeOffset now) => new()
    {
        HostId = hostId,
        Why = Cut(why, MaxWhyLength),
        FatalErrorsAtOpen = fatalErrors,
        LogCollectionId = logCollectionId,
        OpenedUtc = now,
    };

    public void Reported(string likelyCause, string report, bool writtenByAi, DateTimeOffset now)
    {
        LikelyCause = Cut(likelyCause, MaxCauseLength);
        Report = Cut(report, MaxReportLength);
        WrittenByAi = writtenByAi;
        ReportedUtc = now;
        MarkUpdated();
    }

    public void Alerted(DateTimeOffset now)
    {
        AlertedUtc = now;
        AlertError = null;
        AlertAttempts++;
        MarkUpdated();
    }

    public void AlertFailed(string error)
    {
        AlertError = Cut(error, MaxAlertErrorLength);
        AlertAttempts++;
        MarkUpdated();
    }

    /// <summary>Reported, not yet sent, and not given up on.</summary>
    public bool AlertDue => ReportedUtc is not null && AlertedUtc is null && AlertAttempts < MaxAlertAttempts;

    private static string Cut(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "…";
}
