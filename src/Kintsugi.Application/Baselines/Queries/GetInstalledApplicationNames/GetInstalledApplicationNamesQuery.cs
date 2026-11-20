using FluentValidation;
using MediatR;
using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Domain.Entities;

namespace Kintsugi.Application.Baselines.Queries.GetInstalledApplicationNames;

/// <summary>
/// Suggestions for the "Windows hosts with application X installed" assignment
/// (<c>GET /api/admin/installed-application-names?query=</c>): at most <see cref="MaxResults"/> distinct names
/// from the fleet's reported inventory containing <paramref name="Query"/>, ignoring case.
/// </summary>
public record GetInstalledApplicationNamesQuery(string? Query) : IRequest<IReadOnlyList<string>>
{
    public const int MaxResults = 50;
}

public class GetInstalledApplicationNamesQueryValidator : AbstractValidator<GetInstalledApplicationNamesQuery>
{
    public GetInstalledApplicationNamesQueryValidator()
    {
        RuleFor(q => q.Query).MaximumLength(Baseline.MaxApplicationNameLength);
    }
}

public class GetInstalledApplicationNamesQueryHandler : IRequestHandler<GetInstalledApplicationNamesQuery, IReadOnlyList<string>>
{
    private readonly IInstalledApplicationRepository _applications;

    public GetInstalledApplicationNamesQueryHandler(IInstalledApplicationRepository applications)
    {
        _applications = applications;
    }

    public Task<IReadOnlyList<string>> Handle(GetInstalledApplicationNamesQuery request, CancellationToken cancellationToken) =>
        _applications.SearchNamesAsync(request.Query, GetInstalledApplicationNamesQuery.MaxResults, cancellationToken);
}
