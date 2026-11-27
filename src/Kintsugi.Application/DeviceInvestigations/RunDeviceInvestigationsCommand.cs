using MediatR;
using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Application.DeviceHealth;
using Kintsugi.Application.DeviceReport;
using Kintsugi.Application.HostActivity;
using Kintsugi.Application.LogCollections;
using Kintsugi.Domain.Entities;
using Kintsugi.Domain.Enums;

namespace Kintsugi.Application.DeviceInvestigations;

/// <summary>
/// One pass of the automatic investigation, run every few minutes by <c>DeviceInvestigationBackgroundService</c>.
/// Opens one for each device newly Unstable (asking it for 7 days of warnings and errors), writes the report once the
/// log is in or will not come, and sends the alert. Returns how many investigations moved on.
/// </summary>
/// <remarks>
/// A device gets one investigation per <see cref="Cooldown"/>, or a new one sooner if it logs a fatal hardware error
/// it did not have before: three shutdowns a week is the threshold, and re-alerting every pass while it stays there
/// would bury the channel. A failed AI call falls back to the rules, and a failed alert is tried again next pass, up to
/// <see cref="DeviceInvestigation.MaxAlertAttempts"/> times; neither fails the pass. Anything else (the database) does,
/// and the next pass picks up where it stopped, since every step is saved as it finishes.
/// </remarks>
public record RunDeviceInvestigationsCommand : IRequest<int>
{
    public static readonly TimeSpan Cooldown = TimeSpan.FromDays(7);

    /// <summary>The request expires at 24 hours and a pick-up at 30 minutes (<see cref="LogCollection"/>), so by now
    /// the log has either come or failed; this only covers a row an administrator deleted or a clock that drifted.</summary>
    public static readonly TimeSpan LogWait = TimeSpan.FromHours(26);

    /// <summary>An alert not sent within this long is left on the page: a three-day-old alert is news to nobody.</summary>
    public static readonly TimeSpan AlertWindow = TimeSpan.FromDays(3);

    public static readonly TimeSpan AiTimeout = TimeSpan.FromSeconds(90);

    /// <summary>The most investigations opened in one pass, so a fleet-wide event (a power cut) cannot spend the AI budget
    /// in one go; the rest open on the next passes.</summary>
    public const int MaxOpenedPerPass = 10;

    public const int LogPeriodDays = 7;
}

public class RunDeviceInvestigationsCommandHandler : IRequestHandler<RunDeviceInvestigationsCommand, int>
{
    private readonly IDeviceInvestigationRepository _investigations;
    private readonly IHostRepository _hosts;
    private readonly IHostEventRepository _events;
    private readonly ILogCollectionRepository _collections;
    private readonly IHostEventRecorder _recorder;
    private readonly ISender _sender;
    private readonly IAiProviderSettingsResolver _ai;
    private readonly IDeviceInvestigationClient _client;
    private readonly IChatWebhookClient _chat;
    private readonly IUnitOfWork _unitOfWork;

    public RunDeviceInvestigationsCommandHandler(
        IDeviceInvestigationRepository investigations, IHostRepository hosts, IHostEventRepository events,
        ILogCollectionRepository collections, IHostEventRecorder recorder, ISender sender, IAiProviderSettingsResolver ai,
        IDeviceInvestigationClient client, IChatWebhookClient chat, IUnitOfWork unitOfWork)
    {
        _investigations = investigations;
        _hosts = hosts;
        _events = events;
        _collections = collections;
        _recorder = recorder;
        _sender = sender;
        _ai = ai;
        _client = client;
        _chat = chat;
        _unitOfWork = unitOfWork;
    }

    public async Task<int> Handle(RunDeviceInvestigationsCommand request, CancellationToken cancellationToken)
    {
        var settings = await _investigations.GetSettingsAsync(cancellationToken);
        if (settings is not { Enabled: true })
        {
            return 0;
        }

        var now = DateTimeOffset.UtcNow;
        var moved = await OpenAsync(now, cancellationToken);

        var alertSince = now - RunDeviceInvestigationsCommand.AlertWindow;
        foreach (var investigation in await _investigations.GetUnfinishedAsync(alertSince, cancellationToken))
        {
            if (investigation.ReportedUtc is null)
            {
                if (!await LogSettledAsync(investigation, now, cancellationToken))
                {
                    continue;
                }

                await ReportAsync(investigation, now, cancellationToken);
                await _unitOfWork.SaveChangesAsync(cancellationToken);
                moved++;
            }

            if (investigation.AlertDue && investigation.OpenedUtc >= alertSince && settings.SlackWebhookUrl is not null)
            {
                await AlertAsync(investigation, settings, now, cancellationToken);
                await _unitOfWork.SaveChangesAsync(cancellationToken);
            }
        }

        return moved;
    }

    private async Task<int> OpenAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var unstable = HostHealthAssessor.AssessAll(await _events.GetHealthEventsSinceAsync(now.AddDays(-30), cancellationToken), now)
            .Where(a => a.Health == HostHealth.Unstable);

        var opened = 0;
        foreach (var assessment in unstable)
        {
            if (opened >= RunDeviceInvestigationsCommand.MaxOpenedPerPass)
            {
                break;
            }

            var latest = await _investigations.GetLatestForHostAsync(assessment.HostId, cancellationToken);
            if (latest is not null
                && latest.OpenedUtc > now - RunDeviceInvestigationsCommand.Cooldown
                && assessment.FatalErrors30d <= latest.FatalErrorsAtOpen)
            {
                continue;
            }

            var host = await _hosts.GetByIdAsync(assessment.HostId, cancellationToken);
            if (host is null || host.DeletedAtUtc is not null)
            {
                continue;
            }

            // An administrator's request already waiting is the same log: wait for it rather than be refused for a second.
            var open = await _collections.GetOpenForHostAsync(host.Id, cancellationToken);
            var collectionId = open is not null && open.IsActiveAt(now) ? open.Id : (Guid?)null;
            if (collectionId is null)
            {
                var collection = LogCollection.Request(host.Id, DeviceInvestigation.Requester, RunDeviceInvestigationsCommand.LogPeriodDays, LogCollectionDetail.WarningsAndErrors, now);
                await _collections.AddAsync(collection, cancellationToken);
                await _recorder.LogsRequestedAsync(host.Id, DeviceInvestigation.Requester, $"Last {RunDeviceInvestigationsCommand.LogPeriodDays} days, warnings and errors, for an automatic investigation", now, cancellationToken);
                collectionId = collection.Id;
            }

            await _investigations.AddAsync(DeviceInvestigation.Open(host.Id, assessment.Why, assessment.FatalErrors30d, collectionId, now), cancellationToken);
            await _unitOfWork.SaveChangesAsync(cancellationToken);
            opened++;
        }

        return opened;
    }

    private async Task<bool> LogSettledAsync(DeviceInvestigation investigation, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (investigation.LogCollectionId is not { } id || now - investigation.OpenedUtc > RunDeviceInvestigationsCommand.LogWait)
        {
            return true;
        }

        var collection = await _collections.GetByIdAsync(id, cancellationToken);
        return collection is null || collection.EffectiveStatusAt(now) is LogCollectionStatus.Completed or LogCollectionStatus.Failed;
    }

    private async Task ReportAsync(DeviceInvestigation investigation, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var report = await _sender.Send(new GetDeviceReportQuery(investigation.HostId, 30), cancellationToken);
        var host = await _hosts.GetByIdAsync(investigation.HostId, cancellationToken);
        var (finding, byAi) = await DescribeAsync(report, host?.Model, cancellationToken);
        investigation.Reported(finding.LikelyCause, finding.Report, byAi, now);
    }

    private async Task<(DeviceInvestigationFinding Finding, bool ByAi)> DescribeAsync(DeviceReportDto report, string? model, CancellationToken cancellationToken)
    {
        string note;
        try
        {
            var provider = await _ai.ResolveAsync(cancellationToken);
            // The two agent providers drive a tool-using agent (shell, web), and log lines are untrusted text; only a
            // plain completion is ever handed them, as AiFailureClassifier does with installer output.
            if (provider is null || provider.Provider is AiProvider.GooseCli or AiProvider.ClaudeAgentSdk)
            {
                note = provider is null ? "no AI provider is configured." : "the configured AI provider runs an agent with tools, which is never given device logs.";
            }
            else
            {
                var completed = await _collections.GetLatestCompletedForHostAsync(report.Host.Id, cancellationToken);
                var log = completed is null ? null : LogPayload.Read(completed.Content);

                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(RunDeviceInvestigationsCommand.AiTimeout);
                var answer = await _client.WriteInvestigationAsync(provider, DeviceInvestigationWriter.Prompt(report, log, model), timeout.Token);
                if (DeviceInvestigationWriter.Parse(answer, report.Health.Why) is { } finding)
                {
                    return (finding, true);
                }

                note = "its answer was not in the shape asked for.";
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Over budget, timed out, provider down: the report is still written, from the rules.
            note = ex is OperationCanceledException ? "it did not answer in time." : ex.Message;
        }

        return (DeviceInvestigationWriter.FromRules(report, note), false);
    }

    private async Task AlertAsync(DeviceInvestigation investigation, DeviceInvestigationSettings settings, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var host = await _hosts.GetByIdAsync(investigation.HostId, cancellationToken);
        var link = settings.ConsoleBaseUrl is null ? null : $"{settings.ConsoleBaseUrl}/autonomy/device/{investigation.HostId}";
        try
        {
            await _chat.PostAsync(settings.SlackWebhookUrl!, DeviceInvestigationWriter.ChatText(host?.Hostname ?? "A removed device", investigation, link), cancellationToken);
            investigation.Alerted(now);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            investigation.AlertFailed(ex.Message);
        }
    }
}
