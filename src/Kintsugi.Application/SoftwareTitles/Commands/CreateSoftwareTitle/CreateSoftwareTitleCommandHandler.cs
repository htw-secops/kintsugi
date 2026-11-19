using MediatR;
using Kintsugi.Application.Common.Exceptions;
using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Application.SoftwareTitles.Queries.GetSoftware;
using Kintsugi.Application.UpgradePaths;
using Kintsugi.Domain.Entities;
using Kintsugi.Domain.Enums;
using Kintsugi.Domain.Exceptions;

namespace Kintsugi.Application.SoftwareTitles.Commands.CreateSoftwareTitle;

public class CreateSoftwareTitleCommandHandler : IRequestHandler<CreateSoftwareTitleCommand, SoftwareScreenDto>
{
    private static readonly string[] OperatingSystems = [PlatformBucket.Windows, PlatformBucket.MacOs, PlatformBucket.Linux];

    private readonly ISoftwareTitleRepository _titles;
    private readonly IScriptSecretRepository _secrets;
    private readonly IUpgradePathRepository _upgradePaths;
    private readonly IArtifactSigningService _signing;
    private readonly IUnitOfWork _unitOfWork;
    private readonly ISender _sender;

    public CreateSoftwareTitleCommandHandler(
        ISoftwareTitleRepository titles,
        IScriptSecretRepository secrets,
        IUpgradePathRepository upgradePaths,
        IArtifactSigningService signing,
        IUnitOfWork unitOfWork,
        ISender sender)
    {
        _titles = titles;
        _secrets = secrets;
        _upgradePaths = upgradePaths;
        _signing = signing;
        _unitOfWork = unitOfWork;
        _sender = sender;
    }

    public async Task<SoftwareScreenDto> Handle(CreateSoftwareTitleCommand request, CancellationToken cancellationToken)
    {
        RecognizedPackageManager? manager = null;
        string platform;
        string operatingSystem;

        if (!string.IsNullOrWhiteSpace(request.PackageManager))
        {
            if (!PackageManagerCatalog.TryGet(request.PackageManager.Trim(), out var found))
            {
                throw new DomainException($"'{request.PackageManager}' is not a package manager Kintsugi knows.");
            }

            if (!found.SupportsActions)
            {
                throw new DomainException($"{found.Name} cannot install or remove software from Kintsugi"
                    + (found.Name == PackageManagerCatalog.AppStore ? ": `mas install` needs the App Store's numeric id, and Kintsugi holds the bundle id. Use a custom title." : "."));
            }

            manager = found;
            platform = PlatformBucket.ForPackageManager(found.Name);
            operatingSystem = found.OperatingSystem;
        }
        else
        {
            operatingSystem = OperatingSystems.FirstOrDefault(os => os.Equals(request.Platform?.Trim(), StringComparison.OrdinalIgnoreCase))
                ?? throw new DomainException($"'{request.Platform}' is not a platform a custom title can be for: Windows, macOS or Linux.");
            platform = operatingSystem;
        }

        var name = request.Name.Trim();
        if (await _titles.FindAsync(name, platform, cancellationToken) is not null)
        {
            throw new ConflictException($"'{name}' is already a software title for {platform}.");
        }

        await SoftwareTitleRules.RequireSecretsExistAsync(_secrets, request.SecretNames, cancellationToken);

        var title = SoftwareTitle.Create(
            name, platform, manager?.Name, operatingSystem, request.PackageId, request.DetectAs, request.Mode, request.SecretNames, request.CreatedBy);
        SoftwareTitleRules.Sign(title, _signing);
        await _titles.AddAsync(title, cancellationToken);

        if (manager is not null)
        {
            await SeedPackageManagerRowAsync(title, manager, cancellationToken);
        }

        await _unitOfWork.SaveChangesAsync(cancellationToken);
        return await _sender.Send(new GetSoftwareQuery(), cancellationToken);
    }

    /// <summary>
    /// Gives the manager's bucket a row for this package when it has none, so Upgrade Scripts always
    /// lists the manager's script for a human to Take and Sign — without it, a manager that no host
    /// reports yet would leave the title <c>Missing</c> with no remedy on any screen.
    /// </summary>
    /// <remarks>
    /// Written through <see cref="PackageManagerBucketScript.ResolveAsync"/>, the same as an inventory
    /// report seeds one: the bucket's reviewed text and its existing signature when the bucket has one,
    /// the builder's text unsigned when it has not. Nothing is signed here, and <c>BuildScript()</c> is
    /// called only inside <c>ResolveAsync</c>.
    /// </remarks>
    private async Task SeedPackageManagerRowAsync(SoftwareTitle title, RecognizedPackageManager manager, CancellationToken cancellationToken)
    {
        if (await _upgradePaths.GetAsync(title.Name, title.Platform, cancellationToken) is not null)
        {
            return;
        }

        var bucket = await PackageManagerBucketScript.ResolveAsync(_upgradePaths, manager, cancellationToken);
        var row = UpgradePath.Create(
            title.Name, title.Platform, UpgradePathStatus.Found, latestVersion: null, UpgradeMethod.Script,
            downloadUrl: null, command: null, instructions: null, sourceUrl: null, notes: null,
            script: bucket.Script, applicationIdentifier: title.PackageId);
        row.SetSignatures(bucket.Signature, null);
        await _upgradePaths.AddAsync(row, cancellationToken);
    }
}
