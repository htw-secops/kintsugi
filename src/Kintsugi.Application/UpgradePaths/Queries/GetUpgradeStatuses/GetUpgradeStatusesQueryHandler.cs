using MediatR;
using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Application.SoftwareTitles;
using Kintsugi.Domain.Entities;
using Kintsugi.Domain.Enums;

namespace Kintsugi.Application.UpgradePaths.Queries.GetUpgradeStatuses;

/// <summary>
/// The agent's work list: one upgrade row per installed application with a known path, then one
/// <em>action row</em> per software title that wants something done on this host.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Action rows ride this list rather than a route of their own</strong>, so no agent route and
/// no nginx entry were added, and every agent already verifies everything it runs from here. Each one
/// carries <c>UpdateAvailable = false</c>: an agent that predates actions treats it as an app with
/// nothing to patch and ignores it. New agents act on the <c>Action</c> field instead — Required and
/// Prohibited rows silently on the hourly timer check-in, Self-service rows only when the user picks
/// one from the tray.
/// </para>
/// <para>
/// <strong>Not filtered by <c>ManagedApplication</c>.</strong> That table stops Kintsugi <em>patching</em>
/// an app another tool updates (CrowdStrike updates itself); it says nothing about whether the app should
/// be there, so installing or removing one is still Kintsugi's job. The upgrade rows keep that filter
/// (<c>UpgradePathRepository.GetStatusesAsync</c>).
/// </para>
/// <para>
/// <strong>A Prohibited title drops the upgrade rows of what it detected.</strong> A prohibited app is not
/// patched — patching it would install a newer copy of what is about to be removed — and dropping them
/// keeps the list to one row per name, which every agent assumes. That happens even when the uninstall
/// row itself cannot be served yet (its script is unsigned, say).
/// </para>
/// <para>
/// A row is served only when its script is signed and implements the mode
/// (<see cref="SoftwareTitleScripts"/>), the title carries an action signature, and every secret it names
/// is stored. Anything less would be a row every agent refuses, retried and reported every hour.
/// </para>
/// </remarks>
public class GetUpgradeStatusesQueryHandler : IRequestHandler<GetUpgradeStatusesQuery, IReadOnlyList<UpgradeStatusDto>>
{
    private readonly IUpgradePathRepository _repository;
    private readonly IHostRepository _hosts;
    private readonly IInstalledApplicationRepository _installedApplications;
    private readonly ISoftwareTitleRepository _softwareTitles;
    private readonly IScriptSecretRepository _secrets;

    public GetUpgradeStatusesQueryHandler(
        IUpgradePathRepository repository,
        IHostRepository hosts,
        IInstalledApplicationRepository installedApplications,
        ISoftwareTitleRepository softwareTitles,
        IScriptSecretRepository secrets)
    {
        _repository = repository;
        _hosts = hosts;
        _installedApplications = installedApplications;
        _softwareTitles = softwareTitles;
        _secrets = secrets;
    }

    public async Task<IReadOnlyList<UpgradeStatusDto>> Handle(GetUpgradeStatusesQuery request, CancellationToken cancellationToken)
    {
        // A host in maintenance mode is given no work at all, so its agent patches, installs and removes nothing
        // until the administrator ends it. The agent keeps checking in; only the work list is empty.
        var maintained = await _hosts.GetBySerialNumberAsync(request.SerialNumber, cancellationToken);
        if (maintained?.IsInMaintenance(DateTimeOffset.UtcNow) == true)
        {
            return Array.Empty<UpgradeStatusDto>();
        }

        var statuses = await _repository.GetStatusesAsync(request.SerialNumber, cancellationToken);

        var titles = await _softwareTitles.GetAllAsync(cancellationToken);
        if (titles.Count == 0)
        {
            return statuses;
        }

        var host = maintained;
        if (host is null)
        {
            return statuses;
        }

        // The full inventory, not the rows above: those leave out managed and unresearched apps, and
        // whether software is present cannot depend on whether anyone has researched how to patch it.
        var inventory = await _installedApplications.GetByHostIdAsync(host.Id, cancellationToken);
        var osBucket = PlatformBucket.From(host.OperatingSystem);

        var applicable = titles
            .Where(t => t.AppliesTo(host.Id, osBucket, inventory))
            // Prohibited first, so if two titles of one name both apply, the removal is the row served.
            .OrderBy(t => t.Mode == SoftwareMode.Prohibited ? 0 : 1)
            .ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (applicable.Count == 0)
        {
            return statuses;
        }

        var secrets = (await _secrets.GetAllAsync(cancellationToken)).ToDictionary(s => s.Name, StringComparer.Ordinal);
        var scripts = new SoftwareTitleScripts(_repository);
        var dropped = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var actions = new List<UpgradeStatusDto>();

        foreach (var title in applicable)
        {
            var installed = title.IsInstalledOn(inventory);
            bool selfService;
            switch (title.Mode)
            {
                case SoftwareMode.Required when !installed:
                    selfService = false;
                    break;
                case SoftwareMode.SelfService when !installed:
                    selfService = true;
                    break;
                case SoftwareMode.Prohibited when installed:
                    selfService = false;
                    foreach (var app in title.DetectedIn(inventory))
                    {
                        dropped.Add(app.Name);
                    }

                    break;
                default:
                    continue;
            }

            var row = await BuildActionRowAsync(title, selfService, host, scripts, secrets, cancellationToken);
            if (row is not null)
            {
                actions.Add(row);
            }
        }

        // One row per name, which every agent assumes: an upgrade row that survived wins over an action
        // row of the same name, and the first action row of a name wins over a later one.
        var result = statuses.Where(s => !dropped.Contains(s.ApplicationName)).ToList();
        var names = new HashSet<string>(result.Select(s => s.ApplicationName), StringComparer.OrdinalIgnoreCase);
        result.AddRange(actions.Where(a => names.Add(a.ApplicationName)));
        return result;
    }

    private static async Task<UpgradeStatusDto?> BuildActionRowAsync(
        SoftwareTitle title,
        bool selfService,
        Host host,
        SoftwareTitleScripts scripts,
        IReadOnlyDictionary<string, ScriptSecret> secrets,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(title.ActionSignature))
        {
            return null;
        }

        var script = await scripts.ResolveAsync(title, cancellationToken);
        if (script.State != SoftwareScriptState.Ready)
        {
            return null;
        }

        var rowSecrets = new List<ScriptSecretDto>();
        foreach (var name in title.SecretNames)
        {
            if (!secrets.TryGetValue(name, out var secret))
            {
                return null;
            }

            rowSecrets.Add(new ScriptSecretDto(secret.Name, secret.Value, secret.ValueSignature));
        }

        return new UpgradeStatusDto(
            ApplicationName: title.Name,
            Hostname: host.Hostname,
            SerialNumber: host.SerialNumber,
            // Agents require a string here. There is no installed version for an app being installed,
            // and for one being removed the version is not what the row is about.
            InstalledVersion: "",
            LatestVersion: script.LatestVersion,
            UpdateAvailable: false,
            Status: UpgradePathStatus.Found,
            Method: UpgradeMethod.Script,
            DownloadUrl: null,
            Command: null,
            Instructions: null,
            SourceUrl: null,
            Notes: null,
            CheckedUtc: null,
            Script: script.Script,
            ApplicationIdentifier: title.ApplicationIdentifier,
            ScriptSignature: script.ScriptSignature,
            CommandSignature: null,
            PackageManager: title.PackageManager,
            Action: title.Action,
            SelfService: selfService,
            ActionSignature: title.ActionSignature,
            Secrets: rowSecrets);
    }
}
