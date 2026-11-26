using MediatR;
using Kintsugi.Application.Common.Interfaces;

namespace Kintsugi.Application.UpgradePaths.Queries.GetUpgradePathScript;

public class GetUpgradePathScriptQueryHandler : IRequestHandler<GetUpgradePathScriptQuery, UpgradePathScriptDto?>
{
    private readonly IUpgradePathRepository _repository;

    public GetUpgradePathScriptQueryHandler(IUpgradePathRepository repository)
    {
        _repository = repository;
    }

    // GetAsync, the same lookup the row's instructions panel reads its script through, so the dialog and the
    // panel can never show two different scripts for one row.
    public async Task<UpgradePathScriptDto?> Handle(GetUpgradePathScriptQuery request, CancellationToken cancellationToken) =>
        await _repository.GetAsync(request.ApplicationName, request.Platform, cancellationToken) is { Script: { } script } path
            ? new UpgradePathScriptDto(path.ApplicationName, path.Platform, script)
            : null;
}
