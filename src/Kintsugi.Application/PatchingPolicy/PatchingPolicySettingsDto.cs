using Kintsugi.Domain.Entities;
using Kintsugi.Domain.Enums;

namespace Kintsugi.Application.PatchingPolicy;

/// <summary>
/// The one shape both <c>GET /api/patching-policy</c> (agents, <c>PatchingPolicyController</c>) and
/// <c>GET /api/admin/settings/patching-policy</c> (the admin UI) return, so a field added here reaches
/// both. Mirrored by hand in each agent's <c>RawPolicy</c> (clients/*/src/policy.rs) and in
/// web/lib/data/models/settings_mapper.dart.
/// </summary>
/// <remarks>
/// <see cref="OrganisationName"/> and <see cref="OrganisationLogoPng"/> are read only by the Windows
/// agent, for its end-user popups. The macOS and Linux agents' <c>RawPolicy</c> carries no
/// <c>deny_unknown_fields</c>, so the extra keys are ignored there rather than failing the parse.
/// The logo crosses the wire as base64 because that is how System.Text.Json writes a <c>byte[]</c>.
/// </remarks>
public record PatchingPolicySettingsDto(
    int IntervalValue,
    PatchingTimeUnit IntervalUnit,
    int DelayValue,
    PatchingTimeUnit DelayUnit,
    int MaxDelayCount,
    string? OrganisationName,
    byte[]? OrganisationLogoPng)
{
    public static PatchingPolicySettingsDto FromEntity(PatchingPolicySettings entity) =>
        new(entity.IntervalValue, entity.IntervalUnit, entity.DelayValue, entity.DelayUnit, entity.MaxDelayCount,
            entity.OrganisationName, entity.OrganisationLogoPng);

    // Sensible defaults shown the first time this page loads, before anything has been saved:
    // patch weekly, allow deferring a required restart/reboot by a day at a time, up to 3 times.
    public static PatchingPolicySettingsDto Default() =>
        new(IntervalValue: 7, IntervalUnit: PatchingTimeUnit.Days, DelayValue: 1, DelayUnit: PatchingTimeUnit.Days, MaxDelayCount: 3,
            OrganisationName: null, OrganisationLogoPng: null);
}
