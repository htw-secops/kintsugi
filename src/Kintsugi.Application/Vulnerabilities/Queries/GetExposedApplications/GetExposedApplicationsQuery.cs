using MediatR;

namespace Kintsugi.Application.Vulnerabilities.Queries.GetExposedApplications;

/// <summary>
/// The applications with an open CVE that Kintsugi will not patch itself: either another tool owns them
/// (Managed Elsewhere), or nothing owns them and no update path was found. Marking an application as managed
/// elsewhere never takes it off this list: it only says whom to ask.
/// </summary>
public record GetExposedApplicationsQuery : IRequest<ExposedApplicationsDto>;
