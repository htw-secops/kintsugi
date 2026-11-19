using System.Text.RegularExpressions;
using Kintsugi.Domain.Entities;

namespace Kintsugi.Application.UpgradePaths;

/// <summary>
/// The script CLI's modes, and whether a given script implements one. Every script takes
/// <c>--appName &lt;name&gt; --appId &lt;id&gt;</c> plus exactly one mode.
/// </summary>
/// <remarks>
/// <para>
/// <c>--update-version</c> and <c>--update</c> are the original contract, required of every script
/// (<c>AiUpgradePathResearchClient.ValidateScriptAsync</c>). <c>--install</c> and <c>--uninstall</c>
/// came with software titles and are deliberately <em>not</em> required there, so a script researched
/// only to upgrade an app keeps passing validation. What gates them instead is serving:
/// <c>GetUpgradeStatusesQueryHandler</c> appends an action row only when <see cref="Supports"/> finds the
/// mode in the signed script, and the Software screen reports any other as <c>NoActionMode</c>.
/// </para>
/// <para>
/// Both action modes are idempotent (exit 0 with no change when the host is already in the wanted
/// state), run as SYSTEM/root with no GUI, and are killed by the agent after its <c>ACTION_TIMEOUT</c>.
/// </para>
/// </remarks>
public static class ScriptContract
{
    public const string UpdateVersion = "--update-version";
    public const string Update = "--update";
    public const string Install = "--install";
    public const string Uninstall = "--uninstall";

    /// <summary>The prefix each of a title's secrets is exported under: <c>KINTSUGI_SECRET_CROWDSTRIKE_CID</c>.</summary>
    public const string SecretEnvironmentPrefix = "KINTSUGI_SECRET_";

    /// <summary>The mode an action row's script is run with.</summary>
    public static string ModeFor(string action) => action switch
    {
        SoftwareTitle.InstallAction => Install,
        SoftwareTitle.UninstallAction => Uninstall,
        _ => throw new ArgumentOutOfRangeException(nameof(action), action, "Not a software action.")
    };

    /// <summary>
    /// Whether <paramref name="script"/> mentions <paramref name="mode"/> as a whole token — so
    /// <c>--install</c> is not found in <c>--installed</c> or <c>--install-dir</c>. A text check, like
    /// the validator's: it cannot prove the mode works, only that a script which never heard of it is
    /// not handed it.
    /// </summary>
    public static bool Supports(string? script, string mode) =>
        !string.IsNullOrEmpty(script)
        && Regex.IsMatch(script, "(?<![A-Za-z0-9_-])" + Regex.Escape(mode) + "(?![A-Za-z0-9_-])");
}
