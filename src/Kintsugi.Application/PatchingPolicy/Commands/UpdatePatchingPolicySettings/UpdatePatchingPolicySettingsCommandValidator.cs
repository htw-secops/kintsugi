using FluentValidation;
using Kintsugi.Domain.Entities;

namespace Kintsugi.Application.PatchingPolicy.Commands.UpdatePatchingPolicySettings;

public class UpdatePatchingPolicySettingsCommandValidator : AbstractValidator<UpdatePatchingPolicySettingsCommand>
{
    public UpdatePatchingPolicySettingsCommandValidator()
    {
        RuleFor(x => x.IntervalValue).GreaterThanOrEqualTo(1);
        RuleFor(x => x.IntervalUnit).IsInEnum();
        RuleFor(x => x.DelayValue).GreaterThanOrEqualTo(1);
        RuleFor(x => x.DelayUnit).IsInEnum();
        RuleFor(x => x.MaxDelayCount).GreaterThanOrEqualTo(0);

        // The same bounds PatchingPolicySettings.UpdateBranding enforces, repeated here so a bad
        // value arrives at the admin UI as a field error rather than a bare domain message.
        RuleFor(x => x.OrganisationName)
            .Must(name => name is null || name.Trim().Length <= PatchingPolicySettings.MaxOrganisationNameLength)
            .WithMessage($"Organisation name cannot be longer than {PatchingPolicySettings.MaxOrganisationNameLength} characters.");
        RuleFor(x => x.OrganisationLogoPng)
            .Must(logo => logo is null || logo.Length <= PatchingPolicySettings.MaxOrganisationLogoBytes)
            .WithMessage($"Organisation logo cannot be larger than {PatchingPolicySettings.MaxOrganisationLogoBytes / 1024} KB.")
            .Must(logo => logo is null || logo.Length == 0 || PatchingPolicySettings.IsPng(logo))
            .WithMessage("Organisation logo must be a PNG image.")
            .Must(logo => logo is null || !PatchingPolicySettings.ExceedsLogoPixels(logo))
            .WithMessage($"Organisation logo cannot be wider or taller than {PatchingPolicySettings.MaxOrganisationLogoPixels} pixels.");
    }
}
