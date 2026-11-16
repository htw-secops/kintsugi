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
/// current latest). An unreadable version never resolves anything. A row is also stamped with the
/// time it was checked, resolved or not, so the screen can say "last verified".
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
            var path = paths.FirstOrDefault(p => p.Platform == failure.Platform) ?? paths.FirstOrDefault();

            failure.MarkVerified(nowUtc);
            if (failure.FailureSignature is null)
            {
                // Rows reported before signatures existed pick one up on their next check.
                failure.SetSignature(FailureText.Signature(failure.ApplicationName, failure.FailureCategory, failure.Details));
            }

            if (installed is not null && IsProvablyUpToDate(installed.Version, failure.AttemptedVersion, path?.LatestVersion))
            {
                failure.Resolve(PatchFailureResolution.FalsePositiveAutoResolved);
                resolved++;
            }
        }

        return new ReconcileResult(failures.Count, resolved);
    }

    /// <summary>Installed is at least the version the failed attempt was reaching for, or at least
    /// the path's current latest. Either known target being met is enough; neither being known (or
    /// readable) means no proof.</summary>
    public static bool IsProvablyUpToDate(string installedVersion, string? attemptedVersion, string? latestVersion) =>
        VersionComparer.IsAtLeast(installedVersion, attemptedVersion)
        || VersionComparer.IsAtLeast(installedVersion, latestVersion);
}
