using MediatR;
using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Application.ManagedApplications.Queries.GetManagedApplications;
using Kintsugi.Domain.Entities;
using Kintsugi.Domain.Exceptions;

namespace Kintsugi.Application.ManagedApplications.Commands.UnmarkApplicationManaged;

public class UnmarkApplicationManagedCommandHandler : IRequestHandler<UnmarkApplicationManagedCommand, ManagedApplicationsDto>
{
    private readonly IManagedApplicationRepository _repository;
    private readonly IUnitOfWork _unitOfWork;
    private readonly ISender _sender;

    public UnmarkApplicationManagedCommandHandler(IManagedApplicationRepository repository, IUnitOfWork unitOfWork, ISender sender)
    {
        _repository = repository;
        _unitOfWork = unitOfWork;
        _sender = sender;
    }

    public async Task<ManagedApplicationsDto> Handle(UnmarkApplicationManagedCommand request, CancellationToken cancellationToken)
    {
        var existing = await _repository.FindMatchingAsync(request.ApplicationName, cancellationToken);
        if (existing is not null)
        {
            // Covered by a pattern (CrowdStrike*): there is no row for this one application to remove, and removing
            // the pattern's would bring back every application it matches. Saying so beats doing nothing. Asked for by
            // its own name, the pattern is the row asked for, which is how Managed elsewhere's release button removes it.
            if (existing.NormalizedName != ManagedApplication.Normalize(request.ApplicationName))
            {
                throw new DomainException(
                    $"'{request.ApplicationName}' is managed through the pattern '{existing.ApplicationName}' (owner {existing.Owner}). " +
                    $"Remove or change that pattern on Managed elsewhere to bring back every application it covers.");
            }

            _repository.Remove(existing);
            await _unitOfWork.SaveChangesAsync(cancellationToken);
        }

        return await _sender.Send(new GetManagedApplicationsQuery(), cancellationToken);
    }
}
