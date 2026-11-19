using MediatR;
using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Application.SoftwareTitles.Queries.GetSoftware;
using Kintsugi.Domain.Entities;

namespace Kintsugi.Application.SoftwareTitles.Commands.SetScriptSecret;

public class SetScriptSecretCommandHandler : IRequestHandler<SetScriptSecretCommand, SoftwareScreenDto>
{
    private readonly IScriptSecretRepository _secrets;
    private readonly IArtifactSigningService _signing;
    private readonly IUnitOfWork _unitOfWork;
    private readonly ISender _sender;

    public SetScriptSecretCommandHandler(IScriptSecretRepository secrets, IArtifactSigningService signing, IUnitOfWork unitOfWork, ISender sender)
    {
        _secrets = secrets;
        _signing = signing;
        _unitOfWork = unitOfWork;
        _sender = sender;
    }

    public async Task<SoftwareScreenDto> Handle(SetScriptSecretCommand request, CancellationToken cancellationToken)
    {
        // Signed here, on the administrator's save, and nowhere else: a value written to the database
        // any other way carries a signature over the old value, and every agent refuses the row.
        var signature = _signing.Sign(ScriptSecret.CanonicalTextFor(request.Name, request.Value))!;

        var existing = await _secrets.GetAsync(request.Name, cancellationToken);
        if (existing is null)
        {
            await _secrets.AddAsync(ScriptSecret.Create(request.Name, request.Value, signature, request.UpdatedBy), cancellationToken);
        }
        else
        {
            existing.Set(request.Value, signature, request.UpdatedBy);
        }

        await _unitOfWork.SaveChangesAsync(cancellationToken);
        return await _sender.Send(new GetSoftwareQuery(), cancellationToken);
    }
}
