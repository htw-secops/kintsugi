using MediatR;
using Kintsugi.Application.Common.Exceptions;
using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Application.HostActivity;
using Kintsugi.Application.UpgradePaths;
using Kintsugi.Domain.Entities;
using Kintsugi.Domain.Enums;

namespace Kintsugi.Application.PatchFailures.Commands.ReportPatchFailure;

public class ReportPatchFailureCommandHandler : IRequestHandler<ReportPatchFailureCommand, Unit>
{
    private readonly IHostRepository _hostRepository;
    private readonly IPatchFailureRepository _patchFailureRepository;
    private readonly IUpgradePathRepository _upgradePathRepository;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IHostEventRecorder _events;
    private readonly ISoftwareTitleRepository _softwareTitles;

    public ReportPatchFailureCommandHandler(
        IHostRepository hostRepository,
        IPatchFailureRepository patchFailureRepository,
        IUpgradePathRepository upgradePathRepository,
        IUnitOfWork unitOfWork,
        IHostEventRecorder events,
        ISoftwareTitleRepository softwareTitles)
    {
        _softwareTitles = softwareTitles;
        _hostRepository = hostRepository;
        _patchFailureRepository = patchFailureRepository;
        _upgradePathRepository = upgradePathRepository;
        _unitOfWork = unitOfWork;
        _events = events;
    }

    public async Task<Unit> Handle(ReportPatchFailureCommand request, CancellationToken cancellationToken)
    {
        var host = await _hostRepository.GetBySerialNumberAsync(request.SerialNumber, cancellationToken)
            ?? throw new NotFoundException($"No host is registered with serial number '{request.SerialNumber}'.");

        // The platform bucket comes from re-running the same (host, application) resolution that
        // served this agent its work list, rather than from anything the agent said — see the
        // remarks on PatchFailure for why. Null when nothing resolves any more (the row was deleted
        // between the agent fetching it and the failure being reported), which the screen shows as
        // a failure it cannot offer a fix for.
        var path = await _upgradePathRepository.ResolveForHostAsync(request.SerialNumber, request.ApplicationName, cancellationToken);
        var platform = path?.Platform ?? await CustomTitlePlatformAsync(host.OperatingSystem, request.ApplicationName, cancellationToken);

        var existing = await _patchFailureRepository.GetByHostAndApplicationAsync(host.Id, request.ApplicationName, cancellationToken);

        var category = FailureClassifier.Classify(request.Details);

        // Only the text rules, here. A failure they cannot name (ScriptError) is saved as it is and the AI
        // names it afterwards, in the background (ClassifyPatchFailuresCommand): this is an agent-facing
        // request, the agents give up after 15 seconds, and a report that waits on a model for up to 20 is a
        // report that is lost when the agent hangs up, then sent again and spending another call.
        if (existing is null)
        {
            var opened = PatchFailure.Open(
                host.Id, request.ApplicationName, platform,
                request.InstalledVersion, request.AttemptedVersion, request.Details, request.FailedUtc);
            opened.SetCategory(category);
            opened.SetSignature(FailureText.Signature(request.ApplicationName, category, request.Details));
            await _patchFailureRepository.AddAsync(opened, cancellationToken);
        }
        else if (existing.Resolution == PatchFailureResolution.Outstanding)
        {
            existing.RecordAnotherFailure(platform, request.InstalledVersion, request.AttemptedVersion, request.Details, request.FailedUtc);
        }
        else
        {
            existing.Reopen(platform, request.InstalledVersion, request.AttemptedVersion, request.Details, request.FailedUtc);
        }

        existing?.SetCategory(category);
        existing?.SetSignature(FailureText.Signature(request.ApplicationName, category, request.Details));

        await _events.PatchFailedAsync(
            host.Id, request.ApplicationName, request.InstalledVersion, request.AttemptedVersion, request.Details, request.FailedUtc, cancellationToken);

        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return Unit.Value;
    }

    /// <summary>
    /// The bucket of a <em>custom</em> software title with this name for the host's OS, when nothing in
    /// inventory resolved. A failed install is the one failure whose app is, by definition, not in
    /// inventory, so <c>ResolveForHostAsync</c> cannot find it; the custom title's script is the upgrade
    /// path under (name, OS), and pointing the row there gives Failed Updates a working fix panel.
    /// </summary>
    /// <remarks>
    /// Custom titles only. A package-manager title's failure stays at null: a wrong package id is not a
    /// fault in the manager's script, and a Save from the fix panel would plant a one-off script in the
    /// bucket every package of that manager runs.
    /// </remarks>
    private async Task<string?> CustomTitlePlatformAsync(string? operatingSystem, string applicationName, CancellationToken cancellationToken)
    {
        var title = await _softwareTitles.FindAsync(applicationName, PlatformBucket.From(operatingSystem), cancellationToken);
        return title is { PackageManager: null } ? title.Platform : null;
    }
}
