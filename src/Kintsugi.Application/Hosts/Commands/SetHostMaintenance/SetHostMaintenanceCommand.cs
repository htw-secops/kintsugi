using FluentValidation;
using MediatR;
using Kintsugi.Domain.Entities;

namespace Kintsugi.Application.Hosts.Commands.SetHostMaintenance;

/// <summary>Puts one host in maintenance mode, or takes it out. While a host is in it the server gives it no
/// patch work and refuses Patch now for it.</summary>
/// <param name="On">True starts (or changes) maintenance mode, false ends it.</param>
/// <param name="Until">When it ends by itself. Null lasts until an administrator ends it. Ignored when ending.</param>
/// <param name="Actor">The signed-in administrator, set by the controller and never read from the body.</param>
public record SetHostMaintenanceCommand(Guid HostId, bool On, DateTimeOffset? Until, string? Reason, string Actor) : IRequest;

public class SetHostMaintenanceCommandValidator : AbstractValidator<SetHostMaintenanceCommand>
{
    public SetHostMaintenanceCommandValidator()
    {
        RuleFor(x => x.Actor).NotEmpty();
        RuleFor(x => x.Reason).MaximumLength(Host.MaxMaintenanceReasonLength);
        RuleFor(x => x.Until)
            .Must(u => u is null || u > DateTimeOffset.UtcNow)
            .When(x => x.On)
            .WithMessage("The end of maintenance mode must be in the future.");
    }
}
