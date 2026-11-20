using FluentValidation;
using Kintsugi.Domain.Entities;

namespace Kintsugi.Application.SoftwareTitles.Commands.SetScriptSecret;

public class SetScriptSecretCommandValidator : AbstractValidator<SetScriptSecretCommand>
{
    public SetScriptSecretCommandValidator()
    {
        RuleFor(c => c.Name).Must(ScriptSecret.IsValidName).WithMessage(ScriptSecret.NameRule);
        // The message never echoes the value: validation errors are returned to the browser and logged.
        RuleFor(c => c.Value).NotEmpty().WithMessage("A secret needs a value.")
            .MaximumLength(ScriptSecret.MaxValueLength).WithMessage($"A secret value can be at most {ScriptSecret.MaxValueLength} characters.");
    }
}
