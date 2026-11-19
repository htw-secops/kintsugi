using FluentValidation;
using Kintsugi.Domain.Entities;

namespace Kintsugi.Application.ManagedApplications.Commands.MarkApplicationsManaged;

public class MarkApplicationsManagedCommandValidator : AbstractValidator<MarkApplicationsManagedCommand>
{
    /// <summary>More than this in one request is a list somebody should be reading, not a click.</summary>
    public const int MaxNames = 500;

    /// <summary>A pattern needs at least this many characters that are not <c>*</c>. A bare <c>*</c> or <c>a*</c> would
    /// take every application, or most of them, out of Kintsugi's hands fleet-wide without a word.</summary>
    public const int MinPatternLiteralCharacters = 4;

    public MarkApplicationsManagedCommandValidator()
    {
        RuleFor(x => x.Names).NotEmpty().WithMessage("Choose at least one application.");
        RuleFor(x => x.Names.Count).LessThanOrEqualTo(MaxNames).When(x => x.Names is not null)
            .WithMessage($"At most {MaxNames} applications can be marked at once.");
        RuleForEach(x => x.Names).NotEmpty().MaximumLength(ManagedApplication.MaxNameLength);
        RuleForEach(x => x.Names)
            .Must(name => name is null || !name.Contains('*') || ManagedOwnerLookup.Literals(name.Trim()) >= MinPatternLiteralCharacters)
            .WithMessage($"A pattern needs at least {MinPatternLiteralCharacters} characters besides '*', so it cannot match every application.");
        RuleFor(x => x.Owner).NotEmpty().WithMessage("Say who patches it.").MaximumLength(ManagedApplication.MaxOwnerLength);
    }
}
