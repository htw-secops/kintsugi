using System.Text.Json;
using Kintsugi.Domain.Entities;
using Kintsugi.Domain.Enums;

namespace Kintsugi.Application.Hosts;

public record HostDto(
    Guid Id,
    string Hostname,
    string SerialNumber,
    string? OperatingSystem,
    string? IpAddress,
    HostStatus Status,
    DateTimeOffset? LastSeenUtc,
    string? AgentVersion,
    bool? OperatingSystemUpdateAvailable,
    string? OperatingSystemLatestVersion,
    int AppUpdatesAvailableCount,
    int UnpatchedCveCount,
    int PatchedCveCount,
    bool RemovalRequested,
    DateTimeOffset? RestartRequiredSinceUtc = null,
    string? RestartRequiredBecause = null,
    string? LatestAgentVersion = null,
    AgentUpdateState AgentUpdateState = AgentUpdateState.Unknown,
    string? Organization = null,
    string? Department = null,
    string? Location = null,
    string? Model = null,
    string? AssignedShortName = null,
    bool HasAssignedPerson = false,
    string Health = HostStanding.Healthy,
    string? HealthReason = null,
    int FailedPatchCount = 0,
    bool Unstable = false,
    string? LastLoggedInUser = null,
    bool InMaintenance = false,
    DateTimeOffset? MaintenanceUntilUtc = null,
    string? MaintenanceReason = null,
    string? PatchCompliance = null,
    string? PatchComplianceReason = null,
    JsonElement? WindowsUpdateState = null)
{
    /// <summary>
    /// <c>Status</c> is <see cref="Host.StatusAt"/> as of now, not the stored column: the column
    /// only ever records that the host was online at its last check-in, and the Hosts screen
    /// needs to know whether it still is.
    /// </summary>
    /// <remarks>
    /// <c>Health</c>, <c>FailedPatchCount</c> and <c>Unstable</c> are only complete when the caller passes the open
    /// patch failures and the device-health verdict, as the Hosts list does. A caller that cannot (a single host by
    /// id) gets the part that comes from the host itself: its agent and when it was last seen.
    /// </remarks>
    public static HostDto FromEntity(
        Host host,
        int appUpdatesAvailableCount = 0,
        int unpatchedCveCount = 0,
        int patchedCveCount = 0,
        string? latestAgentVersion = null,
        AgentUpdateState agentUpdateState = AgentUpdateState.Unknown,
        int failedPatchCount = 0,
        bool unstable = false,
        IReadOnlyList<WindowsPatchBaseline>? windowsBaselines = null)
    {
        var now = DateTimeOffset.UtcNow;
        // Null baselines means the caller did not load them (a single host by id): no verdict, not "compliant".
        var compliance = windowsBaselines is null
            ? null
            : WindowsPatchCompliance.Of(host.OperatingSystem, host.OperatingSystemVersionId, host.LastSeenUtc, windowsBaselines, now);
        var standing = HostStanding.Of(failedPatchCount, agentUpdateState, host.LastSeenUtc, now, compliance);

        return new(
            host.Id,
            host.Hostname,
            host.SerialNumber,
            host.OperatingSystem,
            host.IpAddress,
            host.StatusAt(DateTimeOffset.UtcNow),
            host.LastSeenUtc,
            host.AgentVersion,
            host.OperatingSystemUpdateAvailable,
            host.OperatingSystemLatestVersion,
            appUpdatesAvailableCount,
            unpatchedCveCount,
            patchedCveCount,
            host.RemovalRequested,
            host.RestartRequiredSinceUtc,
            host.RestartRequiredBecause,
            latestAgentVersion,
            agentUpdateState,
            host.Organization,
            host.Department,
            host.Location,
            host.Model,
            // Only the short form leaves the server here. The full name and the email are read through a Reveal.
            AssignedPerson.ShortName(host.AssignedName),
            host.AssignedName is not null || host.AssignedEmail is not null,
            standing.State,
            standing.Reason,
            failedPatchCount,
            unstable,
            host.LastLoggedInUser,
            host.IsInMaintenance(DateTimeOffset.UtcNow),
            host.MaintenanceUntilUtc,
            host.MaintenanceReason,
            compliance?.State,
            compliance?.Reason,
            host.WindowsUpdateStateJson is { } json ? JsonSerializer.Deserialize<JsonElement>(json) : null);
    }
}
