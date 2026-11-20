using FluentValidation;
using Kintsugi.Domain.Entities;

namespace Kintsugi.Application.SoftwareTitles.Commands.CreateSoftwareTitle;

public class CreateSoftwareTitleCommandValidator : AbstractValidator<CreateSoftwareTitleCommand>
{
    public CreateSoftwareTitleCommandValidator()
    {
        RuleFor(c => c.Name).NotEmpty().MaximumLength(SoftwareTitle.MaxNameLength);
        RuleFor(c => c.PackageManager).MaximumLength(64);
        RuleFor(c => c.Platform).MaximumLength(32);
        RuleFor(c => c.Platform).NotEmpty().When(c => string.IsNullOrWhiteSpace(c.PackageManager))
            .WithMessage("A custom title needs a platform: Windows, macOS or Linux.");
        RuleFor(c => c.PackageId).MaximumLength(SoftwareTitle.MaxPackageIdLength);
        RuleFor(c => c.PackageId).NotEmpty().When(c => !string.IsNullOrWhiteSpace(c.PackageManager))
            .WithMessage("A package-manager title needs the package id the manager knows it by.");
        RuleFor(c => c.DetectAs).MaximumLength(SoftwareTitle.MaxDetectAsLength);
        RuleFor(c => c.Mode).IsInEnum();
        RuleFor(c => c.SecretNames).ValidSecretNames();
    }
}
