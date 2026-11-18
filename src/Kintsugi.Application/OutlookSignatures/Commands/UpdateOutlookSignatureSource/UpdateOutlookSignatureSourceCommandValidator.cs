using FluentValidation;
using Kintsugi.Domain.Entities;

namespace Kintsugi.Application.OutlookSignatures.Commands.UpdateOutlookSignatureSource;

public class UpdateOutlookSignatureSourceCommandValidator : AbstractValidator<UpdateOutlookSignatureSourceCommand>
{
    public UpdateOutlookSignatureSourceCommandValidator()
    {
        // These repeat OutlookSignatureSourceSettings' own invariants, as UpdateVantaSettingsCommandValidator
        // does Vanta's: the entity keeps them true whoever writes, and this turns a bad save into a message
        // under the right box.
        RuleFor(c => c.EndpointUrl)
            .MaximumLength(OutlookSignatureSourceSettings.MaxUrlLength)
            .Must(u => OutlookSignatureSourceSettings.IsHttpsUrl(u!.Trim()))
            .WithMessage("Enter an absolute https:// URL: the ID token is sent to it.")
            .When(c => !string.IsNullOrWhiteSpace(c.EndpointUrl));

        RuleFor(c => c.Audience).MaximumLength(OutlookSignatureSourceSettings.MaxAudienceLength);

        RuleFor(c => c.WindowsUsernameSuffix)
            .Must(OutlookSignatureSourceSettings.IsValidSuffix)
            .WithMessage($"At most {OutlookSignatureSourceSettings.MaxWindowsUsernameSuffixLength} letters, digits, '.', '_' or '-'.");

        RuleFor(c => c.EndpointUrl).NotEmpty().WithMessage("An endpoint URL is required to enable the pull.").When(c => c.Enabled);
        RuleFor(c => c.Audience).NotEmpty().WithMessage("An audience is required to enable the pull.").When(c => c.Enabled);
    }
}
