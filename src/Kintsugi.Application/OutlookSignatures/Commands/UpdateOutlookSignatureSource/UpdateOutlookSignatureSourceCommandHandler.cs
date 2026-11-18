using MediatR;
using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Application.OutlookSignatures.Queries.GetOutlookSignatureSource;
using Kintsugi.Domain.Entities;

namespace Kintsugi.Application.OutlookSignatures.Commands.UpdateOutlookSignatureSource;

public class UpdateOutlookSignatureSourceCommandHandler : IRequestHandler<UpdateOutlookSignatureSourceCommand, OutlookSignatureSourceDto>
{
    private readonly IOutlookSignatureSourceSettingsRepository _repository;
    private readonly ISender _sender;
    private readonly IUnitOfWork _unitOfWork;

    public UpdateOutlookSignatureSourceCommandHandler(IOutlookSignatureSourceSettingsRepository repository, ISender sender, IUnitOfWork unitOfWork)
    {
        _repository = repository;
        _sender = sender;
        _unitOfWork = unitOfWork;
    }

    public async Task<OutlookSignatureSourceDto> Handle(UpdateOutlookSignatureSourceCommand request, CancellationToken cancellationToken)
    {
        var settings = await _repository.GetAsync(cancellationToken);
        if (settings is null)
        {
            settings = OutlookSignatureSourceSettings.Create(request.Enabled, request.EndpointUrl, request.Audience, request.WindowsUsernameSuffix);
            await _repository.AddAsync(settings, cancellationToken);
        }
        else
        {
            settings.Update(request.Enabled, request.EndpointUrl, request.Audience, request.WindowsUsernameSuffix);
        }

        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return await _sender.Send(new GetOutlookSignatureSourceQuery(), cancellationToken);
    }
}
