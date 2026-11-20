using Kintsugi.Application.UpgradePaths;
using Kintsugi.Domain.Entities;
using Kintsugi.Domain.Enums;

namespace Kintsugi.Application.SoftwareTitles;

/// <summary>Where one host stands against one software title.</summary>
public enum SoftwareHostState
{
    /// <summary>The title is not assigned to the host, or the host runs another OS.</summary>
    NotAssigned,

    /// <summary>Assigned, but the host lacks the title's package manager. Never a failure.</summary>
    NotApplicable,

    /// <summary>Detected for Required and Self-service; absent for Prohibited.</summary>
    Compliant,

    /// <summary>Not compliant, with an outstanding Failed Updates row for the title's name.</summary>
    Failed,

    /// <summary>Not compliant, and nothing has failed yet.</summary>
    Pending
}

/// <summary>
/// The one rule for whether a host is where a title wants it — what the Software screen counts and what
/// Run now picks its hosts from, so the two cannot disagree.
/// </summary>
public static class SoftwareCompliance
{
    public static SoftwareHostState StateOf(
        SoftwareTitle title, Host host, IReadOnlyCollection<InstalledApplication> inventory, bool hasOutstandingFailure)
    {
        if (!title.IsAssignedTo(host.Id, PlatformBucket.From(host.OperatingSystem)))
        {
            return SoftwareHostState.NotAssigned;
        }

        if (!title.HasPackageManager(inventory))
        {
            return SoftwareHostState.NotApplicable;
        }

        var installed = title.IsInstalledOn(inventory);
        var compliant = title.Mode == SoftwareMode.Prohibited ? !installed : installed;
        if (compliant)
        {
            return SoftwareHostState.Compliant;
        }

        return hasOutstandingFailure ? SoftwareHostState.Failed : SoftwareHostState.Pending;
    }
}
