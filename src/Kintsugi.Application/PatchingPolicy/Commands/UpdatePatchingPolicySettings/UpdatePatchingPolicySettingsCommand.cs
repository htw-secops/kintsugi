using MediatR;
using Kintsugi.Domain.Enums;

namespace Kintsugi.Application.PatchingPolicy.Commands.UpdatePatchingPolicySettings;

public record UpdatePatchingPolicySettingsCommand(
    int IntervalValue,
    PatchingTimeUnit IntervalUnit,
    int DelayValue,
    PatchingTimeUnit DelayUnit,
    int MaxDelayCount,
    // Null clears each one. The admin UI always sends both (see patching_policy_bloc.dart's save),
    // so the defaults only spare the tests and any caller that predates the branding.
    string? OrganisationName = null,
    byte[]? OrganisationLogoPng = null) : IRequest<PatchingPolicySettingsDto>;
