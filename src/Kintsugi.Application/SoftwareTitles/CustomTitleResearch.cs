using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Application.UpgradePaths;

namespace Kintsugi.Application.SoftwareTitles;

/// <summary>
/// Lets the Applications screen's instructions panel research a <em>custom</em> software title's script
/// before any host has the app — which is the normal case for something about to be installed, and how
/// an administrator pastes a NinjaOne install script into the instructions and has the AI convert it.
/// </summary>
/// <remarks>
/// Both <c>GetUpgradePathPromptQueryHandler</c> and <c>RefreshUpgradePathCommandHandler</c> plan from
/// inventory, and an app nobody has installed is in nobody's inventory, so both used to answer "not
/// currently installed on any host". When that happens and a custom title exists for the name, the work
/// item is built from the title instead. Package-manager titles never need this: their script is the
/// manager's, written by the server.
/// </remarks>
public static class CustomTitleResearch
{
    private static readonly string[] OperatingSystems = [PlatformBucket.Windows, PlatformBucket.MacOs, PlatformBucket.Linux];

    /// <summary>A Research work item for each custom title named <paramref name="applicationName"/> on
    /// <paramref name="platform"/>, or on any OS when that is null. Empty when there is none.</summary>
    public static async Task<IReadOnlyList<UpgradePathWorkItem>> WorkItemsAsync(
        ISoftwareTitleRepository titles, string applicationName, string? platform, CancellationToken cancellationToken)
    {
        var items = new List<UpgradePathWorkItem>();
        foreach (var os in platform is null ? OperatingSystems : [platform])
        {
            var title = await titles.FindAsync(applicationName, os, cancellationToken);
            if (title is { PackageManager: null })
            {
                // No application identifier: the prompt would describe it as the uninstall-registry key
                // or bundle id, and before the install nobody knows that. The script is handed the title
                // name as --appId and finds the app itself.
                items.Add(new UpgradePathWorkItem(title.Name, title.Platform, Array.Empty<string>(), UpgradePathWorkKind.Research, null));
            }
        }

        return items;
    }

    /// <summary>The secret names of the custom title stored under (name, platform), so the AI prompt can
    /// tell the script which <c>KINTSUGI_SECRET_*</c> variables it will be given. Empty when there is no
    /// such title.</summary>
    public static async Task<IReadOnlyList<string>> SecretNamesAsync(
        ISoftwareTitleRepository titles, string applicationName, string platform, CancellationToken cancellationToken)
    {
        if (PlatformBucket.IsPackageManagerBucket(platform))
        {
            return Array.Empty<string>();
        }

        var title = await titles.FindAsync(applicationName, platform, cancellationToken);
        return title is { PackageManager: null } ? title.SecretNames : Array.Empty<string>();
    }
}
