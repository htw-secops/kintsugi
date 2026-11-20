using MediatR;
using Kintsugi.Application.Common.Exceptions;
using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Application.SoftwareTitles.Queries.GetSoftware;

namespace Kintsugi.Application.SoftwareTitles.Commands.DeleteScriptSecret;

public class DeleteScriptSecretCommandHandler : IRequestHandler<DeleteScriptSecretCommand, SoftwareScreenDto>
{
    private readonly IScriptSecretRepository _secrets;
    private readonly ISoftwareTitleRepository _titles;
    private readonly IUnitOfWork _unitOfWork;
    private readonly ISender _sender;

    public DeleteScriptSecretCommandHandler(IScriptSecretRepository secrets, ISoftwareTitleRepository titles, IUnitOfWork unitOfWork, ISender sender)
    {
        _secrets = secrets;
        _titles = titles;
        _unitOfWork = unitOfWork;
        _sender = sender;
    }

    public async Task<SoftwareScreenDto> Handle(DeleteScriptSecretCommand request, CancellationToken cancellationToken)
    {
        var secret = await _secrets.GetAsync(request.Name, cancellationToken)
            ?? throw new NotFoundException($"No secret is stored as '{request.Name}'.");

        if (await _titles.AnyUsesSecretAsync(secret.Name, cancellationToken))
        {
            throw new ConflictException($"'{secret.Name}' is used by a software title. Remove it from every title first.");
        }

        _secrets.Remove(secret);
        await _unitOfWork.SaveChangesAsync(cancellationToken);
        return await _sender.Send(new GetSoftwareQuery(), cancellationToken);
    }
}
