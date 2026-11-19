using MediatR;
using Kintsugi.Application.Common.Exceptions;
using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Application.SoftwareTitles.Queries.GetSoftware;
using Kintsugi.Application.UpgradePaths;
using Kintsugi.Domain.Exceptions;

namespace Kintsugi.Application.SoftwareTitles.Commands.AssignSoftwareTitle;

/// <remarks>The validation is <c>AssignBaselineCommandHandler</c>'s, copied rather than shared so the
/// two can change apart: all or nothing, and every named host must be on the title's OS.</remarks>
public class AssignSoftwareTitleCommandHandler : IRequestHandler<AssignSoftwareTitleCommand, SoftwareScreenDto>
{
    private readonly ISoftwareTitleRepository _titles;
    private readonly IHostRepository _hosts;
    private readonly IUnitOfWork _unitOfWork;
    private readonly ISender _sender;

    public AssignSoftwareTitleCommandHandler(ISoftwareTitleRepository titles, IHostRepository hosts, IUnitOfWork unitOfWork, ISender sender)
    {
        _titles = titles;
        _hosts = hosts;
        _unitOfWork = unitOfWork;
        _sender = sender;
    }

    public async Task<SoftwareScreenDto> Handle(AssignSoftwareTitleCommand request, CancellationToken cancellationToken)
    {
        var title = await _titles.GetAsync(request.Id, cancellationToken)
            ?? throw new NotFoundException($"No software title with id '{request.Id}'.");

        var hostIds = new List<Guid>();
        var unknown = new List<string>();
        var wrongOs = new List<string>();
        foreach (var name in request.HostNames.Select(n => n.Trim()).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var host = await _hosts.GetByHostnameAsync(name, cancellationToken);
            if (host is null || host.DeletedAtUtc is not null)
            {
                unknown.Add(name);
            }
            // A host of another OS would never be served the title (SoftwareTitle.IsAssignedTo checks the
            // OS too), so naming one is a mistake worth saying out loud rather than a silent no-op.
            else if (!PlatformBucket.From(host.OperatingSystem).Equals(title.OperatingSystem, StringComparison.OrdinalIgnoreCase))
            {
                wrongOs.Add($"{name} ({host.OperatingSystem ?? "unknown OS"})");
            }
            else
            {
                hostIds.Add(host.Id);
            }
        }

        // All or nothing: assigning to "most of" a list someone typed would quietly leave a device out.
        var problems = new List<string>();
        if (unknown.Count > 0)
        {
            problems.Add("No host is registered as: " + string.Join(", ", unknown) + ".");
        }

        if (wrongOs.Count > 0)
        {
            problems.Add($"'{title.Name}' is for {title.OperatingSystem} hosts, and these are not: " + string.Join(", ", wrongOs) + ".");
        }

        if (problems.Count > 0)
        {
            throw new DomainException(string.Join(" ", problems) + " Nothing was assigned.");
        }

        title.Assign(request.AllHosts, hostIds, request.AssignedBy);
        await _unitOfWork.SaveChangesAsync(cancellationToken);
        return await _sender.Send(new GetSoftwareQuery(), cancellationToken);
    }
}
