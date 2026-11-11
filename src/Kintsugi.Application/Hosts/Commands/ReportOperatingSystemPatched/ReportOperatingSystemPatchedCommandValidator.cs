using FluentValidation;

namespace Kintsugi.Application.Hosts.Commands.ReportOperatingSystemPatched;

public class ReportOperatingSystemPatchedCommandValidator : AbstractValidator<ReportOperatingSystemPatchedCommand>
{
    public ReportOperatingSystemPatchedCommandValidator()
    {
        RuleFor(x => x.SerialNumber).NotEmpty().MaximumLength(128);
        RuleFor(x => x.RestartBecause).MaximumLength(Kintsugi.Domain.Entities.Host.MaxRestartBecauseLength);
    }
}
