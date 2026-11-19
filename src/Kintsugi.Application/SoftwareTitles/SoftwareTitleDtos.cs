using System.Text.Json.Serialization;
using Kintsugi.Domain.Enums;

namespace Kintsugi.Application.SoftwareTitles;

/// <summary>
/// Whether a title's script can act. Travels as its name. Mirrored by hand in the admin UI's Software
/// screen; see <c>.claude/rules/hand-mirrored-dtos.md</c>.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SoftwareScriptState
{
    /// <summary>Signed, and implements this title's mode. Agents are given action rows for it.</summary>
    Ready,

    /// <summary>A script exists but nobody has signed it. A package-manager title's remedy is Take + Sign
    /// on Upgrade Scripts; a custom title's is the instructions panel's Sign.</summary>
    Unsigned,

    /// <summary>Signed, but it does not implement <c>--install</c>/<c>--uninstall</c> — a manager script
    /// signed before software titles existed. Take the server-written one and sign it.</summary>
    NoActionMode,

    /// <summary>No script at all yet. A custom title's is researched from the instructions panel.</summary>
    Missing
}

/// <summary>Everything the Software screen shows. Every admin call returns the whole of it, so the
/// screen never has to guess what a change did.</summary>
public record SoftwareScreenDto(IReadOnlyList<SoftwareTitleDto> Titles, IReadOnlyList<ScriptSecretSummaryDto> Secrets);

/// <param name="Platform">The OS the title is for — "Windows", "macOS" or "Linux" — for a custom title
/// and a package-manager one alike; <paramref name="PackageManager"/> says which kind it is.</param>
/// <param name="PackageManager">The catalog's name for the manager, or null for a custom title.</param>
/// <param name="CompliantCount">Hosts already in the wanted state: detected for Required and
/// Self-service, absent for Prohibited.</param>
/// <param name="PendingCount">Hosts the title applies to that are not yet compliant and have no
/// outstanding failure.</param>
/// <param name="FailedCount">Hosts with an outstanding Failed Updates row for the title's name.</param>
/// <param name="NotApplicableCount">Assigned hosts without the title's package manager.</param>
public record SoftwareTitleDto(
    Guid Id,
    string Name,
    string Platform,
    string? PackageManager,
    string? PackageId,
    string? DetectAs,
    SoftwareMode Mode,
    IReadOnlyList<string> SecretNames,
    bool AssignedToAll,
    IReadOnlyList<string> AssignedHostNames,
    int CompliantCount,
    int PendingCount,
    int FailedCount,
    int NotApplicableCount,
    SoftwareScriptState ScriptState);

/// <summary>A secret as the admin UI sees it: its name, and who set it when. Never its value — the
/// value is write-only, and only an agent's work list carries it.</summary>
/// <param name="HasValue">Always true for a stored secret today (a secret is created by setting it); kept
/// so the screen follows the settings screens' hasX/blank-keeps pattern.</param>
public record ScriptSecretSummaryDto(string Name, bool HasValue, string? UpdatedBy, DateTimeOffset UpdatedUtc);
