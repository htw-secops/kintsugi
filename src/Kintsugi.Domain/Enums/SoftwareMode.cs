using System.Text.Json.Serialization;

namespace Kintsugi.Domain.Enums;

/// <summary>
/// What a <see cref="Entities.SoftwareTitle"/> wants to be true on the hosts it is assigned to.
/// Travels as its name on the admin API, so reordering it breaks nothing on the wire; the Software
/// screen mirrors the names in <c>web/lib/domain/entities/</c> by hand.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SoftwareMode
{
    /// <summary>Installed silently, at the agent's next hourly check-in, wherever it is missing.</summary>
    Required,

    /// <summary>Offered in the tray's "Install Software" submenu and installed only when the user
    /// picks it. Never acted on at check-in.</summary>
    SelfService,

    /// <summary>Removed silently wherever it is detected, and never patched while it is there.</summary>
    Prohibited
}
