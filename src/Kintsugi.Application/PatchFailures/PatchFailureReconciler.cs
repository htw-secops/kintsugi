using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Application.UpgradePaths;
using Kintsugi.Domain.Entities;
using Kintsugi.Domain.Enums;

namespace Kintsugi.Application.PatchFailures;

/// <summary>How many outstanding failures were compared against inventory, and how many of those
/// turned out to be false positives and were closed.</summary>
public record ReconcileResult(int Checked, int Resolved);

/// <summary>
/// Compares outstanding patch failures with what the host's inventory now says is installed, and
/// closes the ones the inventory proves are not problems.
/// </summary>
/// <remarks>
/// <para>
/// Why this exists: a failure row is opened when an attempt fails, and until now only a success
/// report from the agent closed it. An application that updates itself afterwards (a self-updating
/// cask, a per-user install), or whose "failure" was a package manager exiting non-zero after the
/// upgrade actually worked, stayed on the Failed Updates screen forever with nobody able to act on
/// it.
/// </para>
/// <para>
/// The rule is conservative on purpose: close only when the host's reported version is provably the
/// same as or higher than the version the failed attempt was reaching for (or the upgrade path's
/// current latest), and the agent is not reporting an update still pending for it. An unreadable
/// version never resolves anything. A row is also stamped with the time it was checked, resolved or
/// not, so the screen can say "last verified".
/// </para>
/// <para>
/// It reads the last inventory the agent sent. It does not ask the device — a fresh read waits for
/// the agent's next hourly check-in.
/// </para>
/// </remarks>
public class PatchFailureReconciler
{
    private readonly IPatchFailureRepository _failures;
    private readonly IInstalledApplicationRepository _installed;
    private readonly IUpgradePathRepository _paths;

    public PatchFailureReconciler(
        IPatchFailureRepository failures,
        IInstalledApplicationRepository installed,
        IUpgradePathRepository paths)
    {
        _failures = failures;
        _installed = installed;
        _paths = paths;
    }

    /// <summary>Reconciles every outstanding failure, optionally narrowed to one host and/or one
    /// application. The caller saves.</summary>
    public async Task<ReconcileResult> ReconcileAsync(
        Guid? hostId, string? applicationName, DateTimeOffset nowUtc, CancellationToken cancellationToken)
    {
        var outstanding = hostId is { } id
            ? await _failures.GetOutstandingForHostAsync(id, cancellationToken)
            : await _failures.GetAllOutstandingAsync(cancellationToken);

        return await ReconcileAsync(
            outstanding.Where(f => string.IsNullOrWhiteSpace(applicationName)
                || string.Equals(f.ApplicationName, applicationName, StringComparison.OrdinalIgnoreCase)).ToList(),
            null, nowUtc, cancellationToken);
    }

    /// <summary>
    /// <paramref name="knownInventory"/>, when given, is the inventory to use for every failure
    /// passed in (they must all be one host's) instead of reading it back — the inventory handler
    /// has just replaced that host's rows and not yet saved, so a read would see the old ones.
    /// </summary>
    public async Task<ReconcileResult> ReconcileAsync(
        IReadOnlyList<PatchFailure> failures, IReadOnlyList<InstalledApplication>? knownInventory,
        DateTimeOffset nowUtc, CancellationToken cancellationToken)
    {
        var resolved = 0;
        var inventoryByHost = new Dictionary<Guid, IReadOnlyList<InstalledApplication>>();
        var pathsByName = new Dictionary<string, IReadOnlyList<UpgradePath>>(StringComparer.OrdinalIgnoreCase);

        foreach (var failure in failures)
        {
            if (failure.Resolution != PatchFailureResolution.Outstanding)
            {
                continue;
            }

            if (knownInventory is not null)
            {
                inventoryByHost[failure.HostId] = knownInventory;
            }

            if (!inventoryByHost.TryGetValue(failure.HostId, out var inventory))
            {
                inventory = await _installed.GetByHostIdAsync(failure.HostId, cancellationToken);
                inventoryByHost[failure.HostId] = inventory;
            }

            if (!pathsByName.TryGetValue(failure.ApplicationName, out var paths))
            {
                paths = await _paths.GetAllForApplicationAsync(failure.ApplicationName, cancellationToken);
                pathsByName[failure.ApplicationName] = paths;
            }

            var installed = inventory.FirstOrDefault(a =>
                string.Equals(a.Name, failure.ApplicationName, StringComparison.OrdinalIgnoreCase));
            // Only the path of the platform the failure happened on: another platform's latest version
            // is a different build and may be lower or a different shape, and is no proof about this one.
            // A failure with no platform (its path was gone when it was reported) has only the version
            // its own attempt was reaching for.
            var path = failure.Platform is null
                ? null
                : paths.FirstOrDefault(p => string.Equals(p.Platform, failure.Platform, StringComparison.OrdinalIgnoreCase));

            failure.MarkVerified(nowUtc);
            if (failure.FailureSignature is null)
            {
                // Rows reported before signatures existed pick one up on their next check. They are given the
                // text-rule category first, so the signature is the one a new report of the same problem
                // gets; hashed with a null category it would never match, and one problem would show as
                // two groups. A row the rules cannot name keeps ScriptError until the AI pass settles it,
                // and that pass re-signs it.
                if (failure.FailureCategory is null)
                {
                    failure.SetCategory(FailureClassifier.Classify(failure.Details));
                }

                failure.SetSignature(FailureText.Signature(failure.ApplicationName, failure.FailureCategory, failure.Details));
            }

            // The agent's own "update pending" verdict outranks any version comparison: Snap and Flatpak
            // ship rebuilds under an unchanged version string, so a failed same-version refresh would
            // otherwise read as already done (see InstalledApplication.UpdateAvailable).
            //
            // A per-user-only Windows application is no proof either: its version is the lowest among the
            // users signed in right now, so when user A (who failed to update) signs out, user B's newer copy
            // would make A's real failure read as fixed (see InstalledApplication.PerUserOnly).
            //
            // A failed install or uninstall (a software title's action row) is never auto-resolved. Inventory
            // proves nothing about it: a failed uninstall leaves the app installed, often at the path's latest
            // version, which IsProvablyUpToDate would read as "done", so the row would close and reopen on
            // every hourly retry. Such a failure is closed only by the agent's success report.
            if (installed is not null
                && !IsActionFailure(failure.AttemptedVersion)
                && installed.UpdateAvailable != true
                && !installed.PerUserOnly
                && IsProvablyUpToDate(installed.Version, failure.AttemptedVersion, path?.LatestVersion))
            {
                failure.Resolve(PatchFailureResolution.FalsePositiveAutoResolved);
                resolved++;
            }
        }

        return new ReconcileResult(failures.Count, resolved);
    }

    /// <summary>
    /// The <c>attemptedVersion</c> values the agents send for a software title's action row (IP-881's
    /// <c>report_outcome</c>): "installed" for an install, "removed" for an uninstall. A real version
    /// is never one of these words, so they mark the failure as an action's.
    /// </summary>
    public static readonly IReadOnlySet<string> ActionAttemptedVersions =
        new HashSet<string>(["installed", "removed"], StringComparer.OrdinalIgnoreCase);

    /// <summary>Whether a failure came from a software title's install or uninstall rather than an upgrade.</summary>
    public static bool IsActionFailure(string? attemptedVersion) =>
        attemptedVersion is not null && ActionAttemptedVersions.Contains(attemptedVersion.Trim());

    /// <summary>Installed is at least the version the failed attempt was reaching for, or at least
    /// the path's current latest. Either known target being met is enough; neither being known (or
    /// readable) means no proof.</summary>
    public static bool IsProvablyUpToDate(string installedVersion, string? attemptedVersion, string? latestVersion) =>
        VersionComparer.IsAtLeast(installedVersion, attemptedVersion)
        || VersionComparer.IsAtLeast(installedVersion, latestVersion);
}
