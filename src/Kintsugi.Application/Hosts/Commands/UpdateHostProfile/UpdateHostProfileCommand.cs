using FluentValidation;
using MediatR;
using Kintsugi.Domain.Entities;

namespace Kintsugi.Application.Hosts.Commands.UpdateHostProfile;

/// <summary>
/// Sets the department, location and/or organization of several hosts at once. A field that is null is left alone and
/// an empty one clears it, so "set the department of these twelve" and "clear the location of these three" are the
/// same call.
/// </summary>
/// <param name="Actor">The signed-in administrator, set by the controller and never read from the body.</param>
public record UpdateHostProfileCommand(
    IReadOnlyList<Guid> HostIds,
    string? Organization,
    string? Department,
    string? Location,
    string Actor) : IRequest<int>;

public class UpdateHostProfileCommandValidator : AbstractValidator<UpdateHostProfileCommand>
{
    /// <summary>A page of a large fleet, not the whole of one request body.</summary>
    public const int MaxHosts = 5000;

    public UpdateHostProfileCommandValidator()
    {
        RuleFor(x => x.HostIds).NotEmpty().WithMessage("Choose at least one host.");
        RuleFor(x => x.HostIds.Count).LessThanOrEqualTo(MaxHosts).WithMessage($"At most {MaxHosts} hosts at a time.");
        RuleFor(x => x.Actor).NotEmpty();
        RuleFor(x => x).Must(x => x.Organization is not null || x.Department is not null || x.Location is not null)
            .WithMessage("Say what to change: an organization, a department or a location.");
        RuleFor(x => x.Organization).MaximumLength(Host.MaxProfileTextLength);
        RuleFor(x => x.Department).MaximumLength(Host.MaxProfileTextLength);
        RuleFor(x => x.Location).MaximumLength(Host.MaxProfileTextLength);
    }
}
