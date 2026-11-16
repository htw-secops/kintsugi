using FluentValidation;
using MediatR;
using Kintsugi.Domain.Entities;

namespace Kintsugi.Application.Hosts.Commands.ImportHostProfiles;

/// <summary>One line of an inventory export, reduced to the columns the administrator chose to send.</summary>
public record HostProfileRow(
    string SerialNumber,
    string? Department = null,
    string? Location = null,
    string? Model = null,
    string? AssignedName = null,
    string? AssignedEmail = null);

public record ImportHostProfilesResult(int Matched, int NotFound, int Updated);

/// <summary>
/// Applies an inventory export (Snipe-IT's assets list) to the hosts it names, matched by serial number. The browser has
/// already cut the file down to the columns the administrator chose, so a name or an email is only here when they
/// asked for it; and a field that is empty is ignored, never used to clear what is there.
/// </summary>
/// <param name="Actor">The signed-in administrator, set by the controller and never read from the body.</param>
public record ImportHostProfilesCommand(IReadOnlyList<HostProfileRow> Rows, string Actor) : IRequest<ImportHostProfilesResult>;

public class ImportHostProfilesCommandValidator : AbstractValidator<ImportHostProfilesCommand>
{
    public const int MaxRows = 10000;

    public ImportHostProfilesCommandValidator()
    {
        RuleFor(x => x.Rows).NotEmpty().WithMessage("The file had no rows to import.");
        RuleFor(x => x.Rows.Count).LessThanOrEqualTo(MaxRows).WithMessage($"At most {MaxRows} rows at a time.");
        RuleFor(x => x.Actor).NotEmpty();
        RuleForEach(x => x.Rows).ChildRules(row =>
        {
            row.RuleFor(r => r.SerialNumber).NotEmpty().MaximumLength(128);
            row.RuleFor(r => r.Department).MaximumLength(Host.MaxProfileTextLength);
            row.RuleFor(r => r.Location).MaximumLength(Host.MaxProfileTextLength);
            row.RuleFor(r => r.Model).MaximumLength(Host.MaxModelLength);
            row.RuleFor(r => r.AssignedName).MaximumLength(Host.MaxAssignedNameLength);
            row.RuleFor(r => r.AssignedEmail).MaximumLength(Host.MaxAssignedEmailLength);
        });
    }
}
