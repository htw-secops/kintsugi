using Kintsugi.Domain.Common;
using Kintsugi.Domain.Exceptions;

namespace Kintsugi.Domain.Entities;

/// <summary>
/// A named list of what should be true on a device — Master Daily's registry settings, say — held
/// as <see cref="BaselineVersion"/>s, each a signed document. The baseline itself only carries a
/// name and who it applies to; everything an agent reads comes from a signed version.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Audit, plus an enforce pilot.</strong> An agent fetches the latest signed version, reads each
/// item and reports what it found. An item in <c>enforce</c> mode is also set when it has drifted, and
/// only a value on the application layer's write allowlist can be in that mode, so nothing else on a
/// device is ever written. Rings and exemptions are still to come; the design is "Kintsugi device
/// configuration design" (see IP-841).
/// </para>
/// <para>
/// <strong>Assignment is "every Windows host", a list of hosts, or "Windows hosts with application X
/// installed"</strong>, in any combination. Host ids rather than names, so renaming a machine does not take it out of its baseline;
/// a removed host's id just stops matching anything.
/// </para>
/// </remarks>
public class Baseline : BaseEntity
{
    public const int MaxNameLength = 200;

    public string Name { get; private set; } = default!;

    /// <summary>Who created it, from the session, never from the request body.</summary>
    public string? CreatedBy { get; private set; }

    /// <summary>Applies to every host whose operating system buckets as Windows (see
    /// <c>PlatformBucket</c>), in addition to <see cref="AssignedHostIds"/>.</summary>
    public bool AssignedToAllWindowsHosts { get; private set; }

    public List<Guid> AssignedHostIds { get; private set; } = new();

    public const int MaxApplicationNameLength = 255;

    /// <summary>
    /// Applies to every Windows host whose reported inventory holds an application with exactly this name,
    /// ignoring case — the "hosts with application X installed" target, so a baseline for an add-in follows
    /// the add-in from PC to PC. Null for no such target. In addition to the other two.
    /// </summary>
    public string? AssignedApplicationName { get; private set; }

    public string? AssignedBy { get; private set; }

    private Baseline()
    {
    }

    public static Baseline Create(string name, string? createdBy)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new DomainException("A baseline needs a name.");
        }

        return new Baseline { Name = name.Trim(), CreatedBy = createdBy };
    }

    /// <summary>Replaces who this baseline applies to. An empty assignment is allowed: it is how a
    /// baseline is taken off every device without deleting its history.</summary>
    public void Assign(bool allWindowsHosts, IEnumerable<Guid> hostIds, string? assignedBy, string? applicationName = null)
    {
        var name = string.IsNullOrWhiteSpace(applicationName) ? null : applicationName.Trim();
        if (name is not null && name.Length > MaxApplicationNameLength)
        {
            throw new DomainException($"An application name is at most {MaxApplicationNameLength} characters.");
        }

        AssignedToAllWindowsHosts = allWindowsHosts;
        AssignedHostIds = hostIds.Distinct().ToList();
        AssignedApplicationName = name;
        AssignedBy = assignedBy;
        MarkUpdated();
    }

    /// <summary>Whether this baseline applies to <paramref name="hostId"/>, given whether that host is a
    /// Windows one and whether its inventory holds <see cref="AssignedApplicationName"/> (see
    /// <see cref="IsAssignedApplication"/>). Only Windows hosts match the application target: only the
    /// Windows agent fetches baselines.</summary>
    public bool AppliesTo(Guid hostId, bool hostIsWindows, bool hostHasAssignedApplication = false) =>
        (AssignedToAllWindowsHosts && hostIsWindows)
        || AssignedHostIds.Contains(hostId)
        || (hostIsWindows && AssignedApplicationName is not null && hostHasAssignedApplication);

    /// <summary>Whether <paramref name="installedApplicationNames"/> holds <see cref="AssignedApplicationName"/>,
    /// compared exactly but for case.</summary>
    public bool IsAssignedApplication(IEnumerable<string> installedApplicationNames) =>
        AssignedApplicationName is not null
        && installedApplicationNames.Contains(AssignedApplicationName, StringComparer.OrdinalIgnoreCase);
}
