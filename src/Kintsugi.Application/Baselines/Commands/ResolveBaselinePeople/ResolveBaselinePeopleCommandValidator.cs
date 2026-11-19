using FluentValidation;
using Kintsugi.Domain.Entities;

namespace Kintsugi.Application.Baselines.Commands.ResolveBaselinePeople;

public class ResolveBaselinePeopleCommandValidator : AbstractValidator<ResolveBaselinePeopleCommand>
{
    /// <summary>Far above the sessions one Windows host holds at once; what bounds the work per call.</summary>
    public const int MaxSessions = 64;

    public ResolveBaselinePeopleCommandValidator()
    {
        RuleFor(c => c.SerialNumber).NotEmpty().MaximumLength(255);
        RuleFor(c => c.Sessions).NotNull().Must(s => s is null || s.Count <= MaxSessions)
            .WithMessage($"No more than {MaxSessions} sessions.");
        RuleForEach(c => c.Sessions).NotNull().ChildRules(session =>
        {
            session.RuleFor(s => s.WindowsUser).NotEmpty().MaximumLength(HostPersonResolution.MaxWindowsUserLength);
            session.RuleFor(s => s.GcpwEmail).MaximumLength(PersonSignature.MaxEmailLength);
        });
    }
}
