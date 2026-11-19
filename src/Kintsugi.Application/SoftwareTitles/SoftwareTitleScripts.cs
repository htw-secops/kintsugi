using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Application.UpgradePaths;
using Kintsugi.Domain.Entities;
using Kintsugi.Domain.Enums;

namespace Kintsugi.Application.SoftwareTitles;

/// <summary>
/// Finds the script a software title runs, and says whether it can run it. One place for both the
/// agent's work list (<c>GetUpgradeStatusesQueryHandler</c>, which serves a row only when the state is
/// <see cref="SoftwareScriptState.Ready"/>) and the Software screen (<c>GetSoftwareQueryHandler</c>,
/// which shows the state), so the screen never says Ready about a row the agent is not given.
/// </summary>
/// <remarks>
/// A package-manager title runs whatever its manager's bucket holds signed — the same text every
/// upgrade in that bucket runs (<see cref="IUpgradePathRepository.GetSignedPackageManagerScriptAsync"/>).
/// A custom title runs the upgrade path stored under its own (name, OS bucket), which is the row the
/// Applications screen's instructions panel researches and signs. Nothing here writes or signs.
/// </remarks>
public sealed class SoftwareTitleScripts
{
    private readonly IUpgradePathRepository _upgradePaths;
    private readonly Dictionary<string, PackageManagerBucketScript?> _signedByBucket = new(StringComparer.Ordinal);
    private readonly Dictionary<string, bool> _bucketHasScript = new(StringComparer.Ordinal);

    public SoftwareTitleScripts(IUpgradePathRepository upgradePaths)
    {
        _upgradePaths = upgradePaths;
    }

    public async Task<ResolvedTitleScript> ResolveAsync(SoftwareTitle title, CancellationToken cancellationToken)
    {
        var mode = ScriptContract.ModeFor(title.Action);

        if (title.PackageManager is not null)
        {
            if (!_signedByBucket.TryGetValue(title.Platform, out var signed))
            {
                signed = await _upgradePaths.GetSignedPackageManagerScriptAsync(title.Platform, cancellationToken);
                _signedByBucket[title.Platform] = signed;
            }

            // The seeded or reported row for this package, if any, carries the latest version a
            // check found; the script itself is the bucket's.
            var row = await _upgradePaths.GetAsync(title.Name, title.Platform, cancellationToken);

            if (signed is null || string.IsNullOrEmpty(signed.Signature))
            {
                return new ResolvedTitleScript(
                    await BucketHasScriptAsync(title.Platform, cancellationToken) ? SoftwareScriptState.Unsigned : SoftwareScriptState.Missing,
                    null, null, row?.LatestVersion);
            }

            return ScriptContract.Supports(signed.Script, mode)
                ? new ResolvedTitleScript(SoftwareScriptState.Ready, signed.Script, signed.Signature, row?.LatestVersion)
                : new ResolvedTitleScript(SoftwareScriptState.NoActionMode, null, null, row?.LatestVersion);
        }

        var path = await _upgradePaths.GetAsync(title.Name, title.Platform, cancellationToken);
        if (path?.Script is null)
        {
            return new ResolvedTitleScript(SoftwareScriptState.Missing, null, null, path?.LatestVersion);
        }

        if (string.IsNullOrEmpty(path.ScriptSignature))
        {
            return new ResolvedTitleScript(SoftwareScriptState.Unsigned, null, null, path.LatestVersion);
        }

        return ScriptContract.Supports(path.Script, mode)
            ? new ResolvedTitleScript(SoftwareScriptState.Ready, path.Script, path.ScriptSignature, path.LatestVersion)
            : new ResolvedTitleScript(SoftwareScriptState.NoActionMode, null, null, path.LatestVersion);
    }

    private async Task<bool> BucketHasScriptAsync(string platform, CancellationToken cancellationToken)
    {
        if (!_bucketHasScript.TryGetValue(platform, out var has))
        {
            has = (await _upgradePaths.GetScriptUpgradePathsAsync(platform, cancellationToken)).Any(p => p.Script is not null);
            _bucketHasScript[platform] = has;
        }

        return has;
    }
}

/// <param name="Script">The signed script, only when <paramref name="State"/> is Ready.</param>
/// <param name="ScriptSignature">Its signature, only when Ready.</param>
/// <param name="LatestVersion">The latest version a check has found for the title, when one has.</param>
public sealed record ResolvedTitleScript(SoftwareScriptState State, string? Script, string? ScriptSignature, string? LatestVersion);
