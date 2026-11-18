using Kintsugi.Domain.Common;
using Kintsugi.Domain.Enums;
using Kintsugi.Domain.Exceptions;

namespace Kintsugi.Domain.Entities;

public class Host : BaseEntity
{
    public string Hostname { get; private set; } = default!;
    public string SerialNumber { get; private set; } = default!;
    public string? OperatingSystem { get; private set; }
    public string? IpAddress { get; private set; }
    public HostStatus Status { get; private set; } = HostStatus.Unknown;
    public DateTimeOffset? LastSeenUtc { get; private set; }

    /// <summary>Whether the host's own standard update check (e.g. <c>softwareupdate -l</c> on
    /// macOS) found a pending OS update at last check-in. Null when never reported.</summary>
    public bool? OperatingSystemUpdateAvailable { get; private set; }

    /// <summary>The OS version that check found available, when known. Null if no update is
    /// available, or the reporting agent couldn't determine a version for it.</summary>
    public string? OperatingSystemLatestVersion { get; private set; }

    /// <summary>
    /// The operating system's own machine-readable identifier, when the agent reports one:
    /// os-release's <c>ID</c> on Linux (<c>ubuntu</c>, <c>debian</c>, <c>fedora</c>). Null on
    /// macOS and Windows, where <see cref="OperatingSystem"/> already identifies the product
    /// unambiguously, and null from any agent predating the release that sends it.
    /// </summary>
    /// <remarks>
    /// Reported rather than parsed out of <see cref="OperatingSystem"/> because that string is
    /// <c>PRETTY_NAME</c>, which is prose: "Ubuntu 24.04.1 LTS", "openSUSE Leap 15.6" and
    /// "Alpine v3.20" share no grammar, and the vulnerability assessment needs a stable key to
    /// hang a CPE mapping off. See each agent's <c>system_info::operating_system</c>.
    /// </remarks>
    public string? OperatingSystemId { get; private set; }

    /// <summary>
    /// The operating system's own machine-readable version, when the agent reports one:
    /// os-release's <c>VERSION_ID</c> on Linux (<c>24.04</c>), and the full build with its update
    /// revision on Windows (<c>22631.4317</c>).
    /// </summary>
    /// <remarks>
    /// On Windows this is what makes an OS assessment honest rather than alarming.
    /// <see cref="OperatingSystem"/> carries only the major build — "Windows 11 Pro 23H2 (22631)"
    /// — and NVD's ranges are evaluated against the revision: querying build 22631.4317 returns
    /// 1355 CVEs where 22631.6000 returns 793, so assuming revision zero reports nearly every
    /// Windows CVE ever filed against a fully patched machine. Null from an agent predating the
    /// release that sends it, which <c>OperatingSystemSubject</c> reports as an approximate
    /// assessment rather than silently guessing.
    /// </remarks>
    public string? OperatingSystemVersionId { get; private set; }

    /// <summary>The version of the agent binary that last checked in, as it reports itself
    /// (each agent's <c>RegisterHostRequest.agent_version</c>, from <c>CARGO_PKG_VERSION</c>).
    /// Null until a release that reports one has checked in; older agents omit the field.</summary>
    public string? AgentVersion { get; private set; }

    /// <summary>When this host first reported that it needs a restart for updates it has already
    /// installed, or null when it does not (or has never said). Cleared when the host reports that it
    /// no longer does — which is how the Hosts screen learns that somebody restarted it.</summary>
    public DateTimeOffset? RestartRequiredSinceUtc { get; private set; }

    /// <summary>What asked for the restart, as the host names it ("linux-image-6.8.0-52-generic").
    /// Null when the host did not say, or when no restart is needed.</summary>
    public string? RestartRequiredBecause { get; private set; }

    /// <summary>Longest <see cref="RestartRequiredBecause"/> kept, matching its column.</summary>
    public const int MaxRestartBecauseLength = 500;

    /// <summary>Who was logged on at the last check-in that found anybody, as the agent names them:
    /// the console owner on macOS, each interactive session's account on Windows, each
    /// <c>loginctl</c> user on Linux, comma-separated. Kept rather than cleared when a check-in finds
    /// nobody, so the Hosts search still finds a laptop by its owner while it sits at the login
    /// window. Null until an agent that reports it has found somebody.</summary>
    public string? LastLoggedInUser { get; private set; }

    /// <summary>Longest <see cref="LastLoggedInUser"/> kept, matching its column.</summary>
    public const int MaxLastLoggedInUserLength = 255;

    /// <summary>Set once an admin has asked for this host to be removed — the next check-in's
    /// response tells the agent to uninstall itself completely from the host machine. See
    /// <see cref="RequestRemoval"/> and <see cref="DeletedAtUtc"/>.</summary>
    public bool RemovalRequested { get; private set; }

    /// <summary>Soft-delete marker set the moment removal is requested, not when the agent
    /// eventually confirms it — this is what hides the host from the hosts list immediately. The
    /// row itself is only ever hard-deleted, once that confirmation actually arrives.</summary>
    public DateTimeOffset? DeletedAtUtc { get; private set; }

    /// <summary>Longest department, location or organization kept.</summary>
    public const int MaxProfileTextLength = 100;

    public const int MaxModelLength = 150;
    public const int MaxAssignedNameLength = 150;

    /// <summary>The longest an email address can be (RFC 5321).</summary>
    public const int MaxAssignedEmailLength = 254;

    /// <summary>The organization this host belongs to in the inventory ("HTW"). Set by an administrator or an
    /// import, never by the agent, which cannot know it. Null until someone sets it.</summary>
    public string? Organization { get; private set; }

    /// <summary>The department of the person the device is assigned to, as the inventory (Snipe-IT) names it.</summary>
    public string? Department { get; private set; }

    public string? Location { get; private set; }

    /// <summary>The hardware model ("Surface Laptop 7"). Comes from an import for now; no agent reports it yet.</summary>
    public string? Model { get; private set; }

    /// <summary>Who the device is assigned to. **Optional and personal data**: only ever filled by an import the
    /// administrator chose to include it in. The Hosts list never carries it (only a short form, see
    /// AssignedPerson.ShortName); the full name and the email are read through a reveal that is recorded.</summary>
    public string? AssignedName { get; private set; }

    public string? AssignedEmail { get; private set; }

    private Host()
    {
    }

    public Host(
        string hostname,
        string serialNumber,
        string? operatingSystem = null,
        string? ipAddress = null,
        bool? operatingSystemUpdateAvailable = null,
        string? operatingSystemLatestVersion = null,
        string? agentVersion = null,
        string? operatingSystemId = null,
        string? operatingSystemVersionId = null)
    {
        if (string.IsNullOrWhiteSpace(hostname))
        {
            throw new DomainException("Hostname is required.");
        }

        if (string.IsNullOrWhiteSpace(serialNumber))
        {
            throw new DomainException("Serial number is required.");
        }

        Hostname = hostname;
        SerialNumber = serialNumber;
        OperatingSystem = operatingSystem;
        IpAddress = ipAddress;
        OperatingSystemUpdateAvailable = operatingSystemUpdateAvailable;
        OperatingSystemLatestVersion = operatingSystemLatestVersion;
        AgentVersion = agentVersion;
        OperatingSystemId = operatingSystemId;
        OperatingSystemVersionId = operatingSystemVersionId;
    }

    /// <summary>
    /// Sets the inventory details of this host. For each argument: <c>null</c> keeps what is there, an empty or
    /// blank text clears it, and anything else replaces it. Returns the names of the fields that actually changed
    /// (empty when nothing did), which is what goes on the timeline.
    /// </summary>
    public IReadOnlyList<string> SetProfile(
        string? organization = null,
        string? department = null,
        string? location = null,
        string? model = null,
        string? assignedName = null,
        string? assignedEmail = null)
    {
        var changed = new List<string>();

        string? Apply(string label, string? current, string? requested, int max)
        {
            if (requested is null)
            {
                return current;
            }

            var value = requested.Trim();
            if (value.Length > max)
            {
                throw new DomainException($"{label} is longer than {max} characters.");
            }

            var next = value.Length == 0 ? null : value;
            if (!string.Equals(next, current, StringComparison.Ordinal))
            {
                changed.Add(label);
            }

            return next;
        }

        Organization = Apply("Organization", Organization, organization, MaxProfileTextLength);
        Department = Apply("Department", Department, department, MaxProfileTextLength);
        Location = Apply("Location", Location, location, MaxProfileTextLength);
        Model = Apply("Model", Model, model, MaxModelLength);
        AssignedName = Apply("Assigned name", AssignedName, assignedName, MaxAssignedNameLength);
        AssignedEmail = Apply("Assigned email", AssignedEmail, assignedEmail, MaxAssignedEmailLength);

        if (changed.Count > 0)
        {
            MarkUpdated();
        }

        return changed;
    }

    public void RecordHeartbeat(HostStatus status)
    {
        Status = status;
        LastSeenUtc = DateTimeOffset.UtcNow;
        MarkUpdated();
    }

    /// <summary>
    /// How long a host may go without checking in before it reads as offline. Agents check in
    /// once an hour (see <c>ICheckInLoadBalancer</c>, which spreads those check-ins across the
    /// hour), so two hours means a host has missed one entirely rather than merely run a few
    /// minutes late — a reassigned check-in minute or a slow network must not flicker it offline.
    /// </summary>
    public static readonly TimeSpan OnlineWindow = TimeSpan.FromHours(2);

    /// <summary>
    /// The status to display, as of <paramref name="now"/>. <see cref="Status"/> is what the agent
    /// last reported — every check-in writes <see cref="HostStatus.Online"/>, and nothing ever
    /// writes anything else — so read alone it says a host is online forever after its first
    /// check-in. This ages it: an online host whose <see cref="LastSeenUtc"/> is older than
    /// <see cref="OnlineWindow"/> is <see cref="HostStatus.Offline"/>. Derived at read time rather
    /// than by a background sweep so it is never stale, and pure so it can be pinned by a test.
    /// </summary>
    public HostStatus StatusAt(DateTimeOffset now)
    {
        if (Status != HostStatus.Online)
        {
            return Status;
        }

        return LastSeenUtc is { } lastSeen && now - lastSeen <= OnlineWindow
            ? HostStatus.Online
            : HostStatus.Offline;
    }

    public void Reregister(
        string hostname,
        string? operatingSystem,
        string? ipAddress,
        bool? operatingSystemUpdateAvailable = null,
        string? operatingSystemLatestVersion = null,
        string? agentVersion = null,
        string? operatingSystemId = null,
        string? operatingSystemVersionId = null,
        bool? restartRequired = null,
        string? restartBecause = null)
    {
        if (string.IsNullOrWhiteSpace(hostname))
        {
            throw new DomainException("Hostname is required.");
        }

        Hostname = hostname;

        if (operatingSystem is not null)
        {
            OperatingSystem = operatingSystem;
        }

        if (ipAddress is not null)
        {
            IpAddress = ipAddress;
        }

        // Null means "not reported this time" (an agent that can't run the check at all), left
        // as-is; an explicit false (checked, nothing pending) still overwrites a stale true.
        if (operatingSystemUpdateAvailable is not null)
        {
            OperatingSystemUpdateAvailable = operatingSystemUpdateAvailable;
        }

        if (operatingSystemLatestVersion is not null)
        {
            OperatingSystemLatestVersion = operatingSystemLatestVersion;
        }
        else if (operatingSystemUpdateAvailable == false)
        {
            // A definitive "nothing pending" clears any previously reported target version too,
            // rather than leaving e.g. "15.1" displayed once the host is already caught up.
            OperatingSystemLatestVersion = null;
        }

        // Same null-means-not-reported rule: an older agent that never sends the field must not
        // erase the version a newer build reported before a downgrade or reinstall.
        if (agentVersion is not null)
        {
            AgentVersion = agentVersion;
        }

        // Same null-means-not-reported rule again, and it matters most here: these two arrive only
        // from agents new enough to send them, and blanking them on a mixed-version fleet would
        // downgrade a host's OS assessment back to approximate every time an older sibling binary
        // happened to check in from the same machine.
        if (operatingSystemId is not null)
        {
            OperatingSystemId = operatingSystemId;
        }

        if (operatingSystemVersionId is not null)
        {
            OperatingSystemVersionId = operatingSystemVersionId;
        }

        RecordRestartState(restartRequired, restartBecause, DateTimeOffset.UtcNow);

        RecordHeartbeat(HostStatus.Online);
    }

    /// <summary>
    /// Records what the host says about needing a restart. Null means "not reported" — an agent that
    /// cannot tell (every agent but the Linux one today, and any Linux agent that predates the field) —
    /// and leaves what is stored alone, the same rule as every other optional field here. An explicit
    /// true starts the clock once and keeps it, so "since" is when it was first needed and not the
    /// last check-in; an explicit false clears both.
    /// </summary>
    public void RecordRestartState(bool? restartRequired, string? restartBecause, DateTimeOffset now)
    {
        if (restartRequired is null)
        {
            return;
        }

        if (restartRequired == true)
        {
            RestartRequiredSinceUtc ??= now;
            RestartRequiredBecause = Truncate(restartBecause, MaxRestartBecauseLength);
            return;
        }

        RestartRequiredSinceUtc = null;
        RestartRequiredBecause = null;
    }

    /// <summary>
    /// Records who the host says is logged on. Null or blank means nobody is, or an agent that
    /// predates the field, and leaves the last name alone — see <see cref="LastLoggedInUser"/>. Cut
    /// to the column rather than refused, for the reason <see cref="RecordRestartState"/> gives: a
    /// display string must never fail the check-in that carries it.
    /// </summary>
    public void RecordLoggedInUser(string? loggedInUser)
    {
        if (!string.IsNullOrWhiteSpace(loggedInUser))
        {
            LastLoggedInUser = Truncate(loggedInUser.Trim(), MaxLastLoggedInUserLength);
        }
    }

    /// <summary>
    /// Cuts a display string reported on the check-in to its column width. This is the only length
    /// check those strings get — the validators deliberately do not refuse a long one, because
    /// refusing it refused the whole check-in — so it never leaves half of a surrogate pair at the end.
    /// </summary>
    private static string? Truncate(string? value, int maxLength)
    {
        if (value is null || value.Length <= maxLength)
        {
            return value;
        }

        var length = char.IsHighSurrogate(value[maxLength - 1]) ? maxLength - 1 : maxLength;
        return value[..length];
    }

    /// <summary>
    /// Records that a pending macOS update was just successfully installed — clears the pending
    /// flag and target version immediately, the same way a definitive "nothing pending" from
    /// <see cref="Reregister"/> does, rather than leaving them stale until this host's next
    /// check-in re-derives them from a fresh <c>softwareupdate -l</c> run.
    /// </summary>
    public void RecordOperatingSystemPatched()
    {
        OperatingSystemUpdateAvailable = false;
        OperatingSystemLatestVersion = null;
        RecordHeartbeat(HostStatus.Online);
    }

    /// <summary>
    /// An admin has asked for this host to be removed: hides it from the hosts list immediately
    /// (<see cref="DeletedAtUtc"/>) and flags it so the next check-in's response instructs the
    /// agent to uninstall itself completely — see <see cref="RemovalRequested"/>. Idempotent, so
    /// clicking remove twice (or a slow double-submit) is harmless.
    /// </summary>
    public void RequestRemoval()
    {
        if (DeletedAtUtc is not null)
        {
            return;
        }

        RemovalRequested = true;
        DeletedAtUtc = DateTimeOffset.UtcNow;
        MarkUpdated();
    }
}
