using Kintsugi.Domain.Common;

namespace Kintsugi.Domain.Entities;

/// <summary>
/// The KEV backlog on one day: how many devices still have an exploited CVE (one on CISA's Known Exploited
/// Vulnerabilities list) open, and how many of them are past the 14-day target counted from the day CISA added the CVE. Recorded daily because which
/// devices a CVE affects is worked out when asked and the inventory is rebuilt on every report, so a past day
/// cannot be reconstructed. It starts on the day it is first recorded and is never back-filled.
/// </summary>
/// <remarks>
/// "Within target" and "overdue" are decided on the day by that 14-day target and stored as
/// counts, so nothing later can recolour history.
/// </remarks>
public class KevBacklogSnapshot : BaseEntity
{
    /// <summary>Midnight UTC of the day it describes. Unique.</summary>
    public DateTimeOffset SnapshotDateUtc { get; private set; }

    /// <summary>Devices with at least one exploited CVE open: within target plus overdue.</summary>
    public int DevicesOpen { get; private set; }

    public int DevicesWithinTarget { get; private set; }

    public int DevicesOverdue { get; private set; }

    /// <summary>Within target now, with the 14-day target falling in the next 7 days: the ones about to turn overdue.</summary>
    public int DevicesDueWithin7Days { get; private set; }

    /// <summary>Overdue devices not seen for 7 days or more: patching cannot reach them while they are off.</summary>
    public int OverdueNotSeen7Days { get; private set; }

    /// <summary>The open exploited CVE on the most devices that day, with how many.</summary>
    public string? TopCveId { get; private set; }

    public int TopCveDevices { get; private set; }

    public const int MaxCveIdLength = 32;

    private KevBacklogSnapshot()
    {
    }

    public static KevBacklogSnapshot Record(
        DateTimeOffset dateUtc,
        int devicesOpen,
        int devicesWithinTarget,
        int devicesOverdue,
        int devicesDueWithin7Days,
        int overdueNotSeen7Days,
        string? topCveId,
        int topCveDevices) =>
        new()
        {
            SnapshotDateUtc = new DateTimeOffset(dateUtc.UtcDateTime.Date, TimeSpan.Zero),
            DevicesOpen = devicesOpen,
            DevicesWithinTarget = devicesWithinTarget,
            DevicesOverdue = devicesOverdue,
            DevicesDueWithin7Days = devicesDueWithin7Days,
            OverdueNotSeen7Days = overdueNotSeen7Days,
            TopCveId = topCveId is { Length: > MaxCveIdLength } ? topCveId[..MaxCveIdLength] : topCveId,
            TopCveDevices = topCveDevices,
        };
}
