using MediatR;
using Kintsugi.Application.Common.Exceptions;
using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Application.SoftwareTitles.Queries.GetSoftware;

namespace Kintsugi.Application.SoftwareTitles.Commands.DeleteSoftwareTitle;

public class DeleteSoftwareTitleCommandHandler : IRequestHandler<DeleteSoftwareTitleCommand, SoftwareScreenDto>
{
    private readonly ISoftwareTitleRepository _titles;
    private readonly IUnitOfWork _unitOfWork;
    private readonly ISender _sender;

    public DeleteSoftwareTitleCommandHandler(ISoftwareTitleRepository titles, IUnitOfWork unitOfWork, ISender sender)
    {
        _titles = titles;
        _unitOfWork = unitOfWork;
        _sender = sender;
    }

    public async Task<SoftwareScreenDto> Handle(DeleteSoftwareTitleCommand request, CancellationToken cancellationToken)
    {
        var title = await _titles.GetAsync(request.Id, cancellationToken)
            ?? throw new NotFoundException($"No software title with id '{request.Id}'.");

        _titles.Remove(title);
        await _unitOfWork.SaveChangesAsync(cancellationToken);
        return await _sender.Send(new GetSoftwareQuery(), cancellationToken);
    }
}
