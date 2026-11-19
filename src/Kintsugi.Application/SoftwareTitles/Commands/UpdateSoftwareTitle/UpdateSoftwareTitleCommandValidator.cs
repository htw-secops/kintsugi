using FluentValidation;
using Kintsugi.Domain.Entities;

namespace Kintsugi.Application.SoftwareTitles.Commands.UpdateSoftwareTitle;

public class UpdateSoftwareTitleCommandValidator : AbstractValidator<UpdateSoftwareTitleCommand>
{
    public UpdateSoftwareTitleCommandValidator()
    {
        RuleFor(c => c.Id).NotEmpty();
        RuleFor(c => c.Mode).IsInEnum();
        RuleFor(c => c.DetectAs).MaximumLength(SoftwareTitle.MaxDetectAsLength);
        RuleFor(c => c.SecretNames).ValidSecretNames();
    }
}
