using MediatR;
using Kintsugi.Application.Common.Exceptions;
using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Application.SoftwareTitles.Queries.GetSoftware;

namespace Kintsugi.Application.SoftwareTitles.Commands.UpdateSoftwareTitle;

public class UpdateSoftwareTitleCommandHandler : IRequestHandler<UpdateSoftwareTitleCommand, SoftwareScreenDto>
{
    private readonly ISoftwareTitleRepository _titles;
    private readonly IScriptSecretRepository _secrets;
    private readonly IArtifactSigningService _signing;
    private readonly IUnitOfWork _unitOfWork;
    private readonly ISender _sender;

    public UpdateSoftwareTitleCommandHandler(
        ISoftwareTitleRepository titles, IScriptSecretRepository secrets, IArtifactSigningService signing, IUnitOfWork unitOfWork, ISender sender)
    {
        _titles = titles;
        _secrets = secrets;
        _signing = signing;
        _unitOfWork = unitOfWork;
        _sender = sender;
    }

    public async Task<SoftwareScreenDto> Handle(UpdateSoftwareTitleCommand request, CancellationToken cancellationToken)
    {
        var title = await _titles.GetAsync(request.Id, cancellationToken)
            ?? throw new NotFoundException($"No software title with id '{request.Id}'.");

        await SoftwareTitleRules.RequireSecretsExistAsync(_secrets, request.SecretNames, cancellationToken);

        title.Update(request.Mode, request.DetectAs, request.SecretNames);
        // Re-signed on every save: a change of mode can turn an install into an uninstall, and a change
        // of secrets changes what the agent will export. The old signature must not cover either.
        SoftwareTitleRules.Sign(title, _signing);

        await _unitOfWork.SaveChangesAsync(cancellationToken);
        return await _sender.Send(new GetSoftwareQuery(), cancellationToken);
    }
}
