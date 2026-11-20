using MediatR;
using Kintsugi.Application.Common.Exceptions;
using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Application.ForcedPatchRuns;
using Kintsugi.Application.ForcedPatchRuns.Commands.RequestForcedPatchRuns;
using Kintsugi.Application.SoftwareTitles.Queries.GetSoftware;
using Kintsugi.Domain.Enums;
using Kintsugi.Domain.Exceptions;

namespace Kintsugi.Application.SoftwareTitles.Commands.RunSoftwareTitleNow;

public class RunSoftwareTitleNowCommandHandler : IRequestHandler<RunSoftwareTitleNowCommand, RequestForcedPatchRunsResult>
{
    private readonly ISoftwareTitleRepository _titles;
    private readonly IHostRepository _hosts;
    private readonly IInstalledApplicationRepository _installedApplications;
    private readonly IPatchFailureRepository _patchFailures;
    private readonly IUpgradePathRepository _upgradePaths;
    private readonly ISender _sender;

    public RunSoftwareTitleNowCommandHandler(
        ISoftwareTitleRepository titles,
        IHostRepository hosts,
        IInstalledApplicationRepository installedApplications,
        IPatchFailureRepository patchFailures,
        IUpgradePathRepository upgradePaths,
        ISender sender)
    {
        _titles = titles;
        _hosts = hosts;
        _installedApplications = installedApplications;
        _patchFailures = patchFailures;
        _upgradePaths = upgradePaths;
        _sender = sender;
    }

    public async Task<RequestForcedPatchRunsResult> Handle(RunSoftwareTitleNowCommand request, CancellationToken cancellationToken)
    {
        var title = await _titles.GetAsync(request.Id, cancellationToken)
            ?? throw new NotFoundException($"No software title with id '{request.Id}'.");

        // Agents leave Self-service rows out of forced runs (only the user's pick installs one), so a
        // request here would sit unclaimed until it expired. Saying so beats a "requested" that never acts.
        if (title.Mode == SoftwareMode.SelfService)
        {
            throw new DomainException($"'{title.Name}' is Self-service: users install it from the tray. Run now applies to Required and Prohibited titles.");
        }

        var script = await new SoftwareTitleScripts(_upgradePaths).ResolveAsync(title, cancellationToken);
        if (script.State != SoftwareScriptState.Ready)
        {
            throw new DomainException($"'{title.Name}' has no script an agent will run yet ({script.State}), so there is nothing to run now.");
        }

        // Pending and Failed alike: a failed host is one that still needs the action, and Run now is
        // how an administrator retries it after fixing the cause.
        var fleet = await SoftwareFleet.LoadAsync(_hosts, _installedApplications, _patchFailures, cancellationToken);
        var hostNames = fleet.States(title)
            .Where(s => s.State is SoftwareHostState.Pending or SoftwareHostState.Failed)
            .Select(s => s.Host.Hostname)
            .ToList();

        if (hostNames.Count == 0)
        {
            return new RequestForcedPatchRunsResult(0, Array.Empty<string>());
        }

        return await _sender.Send(
            new RequestForcedPatchRunsCommand(title.Name, title.Platform, hostNames, request.RequestedBy), cancellationToken);
    }
}
