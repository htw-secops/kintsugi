using FluentValidation;

namespace Kintsugi.Application.SoftwareTitles.Commands.AssignSoftwareTitle;

public class AssignSoftwareTitleCommandValidator : AbstractValidator<AssignSoftwareTitleCommand>
{
    /// <summary>Same ceiling, for the same reason, as <c>AssignBaselineCommandValidator.MaxHostNames</c>.</summary>
    public const int MaxHostNames = 5000;

    public AssignSoftwareTitleCommandValidator()
    {
        RuleFor(c => c.Id).NotEmpty();
        RuleFor(c => c.HostNames).NotNull().Must(n => n is null || n.Count <= MaxHostNames)
            .WithMessage($"No more than {MaxHostNames} hosts can be named.");
        RuleForEach(c => c.HostNames).NotEmpty().MaximumLength(255);
    }
}
