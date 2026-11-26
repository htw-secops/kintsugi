namespace Kintsugi.Application.UpgradePaths;

/// <summary>
/// Two facts about one installation that decide whether patching it can ever work, shared by the
/// agent's work list (<c>UpgradePathRepository.GetStatusesAsync</c>) and the failure reconciler
/// (<c>PatchFailureReconciler</c>) so the two never disagree about the same row.
/// </summary>
public static class InstallationRules
{
    /// <summary>
    /// Whether an agent will pass <paramref name="identifier"/> to a script as <c>--appId</c>. The mirror
    /// of each agent's <c>upgrade::reject_unsafe_script_argument</c>: empty, a dot entry, a path
    /// separator or a control character is refused there, after the script has been fetched and the
    /// failure reported. winget names a package it could not match to any source by a local id that
    /// always carries a backslash (<c>ARP\Machine\X64\{GUID}</c>, <c>MSIX\Name_1.0_x64__hash</c>), and
    /// winget cannot upgrade by such an id anyway, so serving one made a failed patch every cycle.
    /// </summary>
    public static bool IsUsableIdentifier(string? identifier) =>
        !string.IsNullOrEmpty(identifier)
        && identifier is not "." and not ".."
        && !identifier.Any(c => c is '/' or '\\' || char.IsControl(c));

    /// <summary>One installation as these rules need to see it.</summary>
    public readonly record struct Installation(
        string Name, string Version, string? Identifier, Guid? ParentApplicationId, bool? UpdateAvailable, bool PerUserOnly);

    /// <summary>
    /// Whether <paramref name="row"/> is an older registration of a package another row on the same host
    /// already holds at <paramref name="target"/> (or <paramref name="alternativeTarget"/>) or newer.
    /// </summary>
    /// <remarks>
    /// A Visual C++ v14 runtime upgraded by a newer bundle can leave the old bundle's uninstall entry
    /// behind ("2015-2019 ... 14.28" beside "v14 ... 14.52"), and winget lists both under one id,
    /// Microsoft.VCRedist.2015+.x64. `winget upgrade` finds the package current and exits 0, the old entry
    /// stays, and the agent reported "still at 14.28" every cycle. Same manager, same catalog id and a
    /// different name is what marks it; the other row must be a version the agent itself vouches for
    /// (no pending-update verdict, not per-user only, which reports the lowest of whoever is signed in).
    /// </remarks>
    public static bool IsSuperseded(
        Installation row, IEnumerable<Installation> sameHost, string? target, string? alternativeTarget = null) =>
        row.ParentApplicationId is not null
        && IsUsableIdentifier(row.Identifier)
        && sameHost.Any(other =>
            other.ParentApplicationId == row.ParentApplicationId
            && string.Equals(other.Identifier, row.Identifier, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(other.Name, row.Name, StringComparison.OrdinalIgnoreCase)
            && other.UpdateAvailable != true
            && !other.PerUserOnly
            && (VersionComparer.IsAtLeast(other.Version, target) || VersionComparer.IsAtLeast(other.Version, alternativeTarget)));
}
