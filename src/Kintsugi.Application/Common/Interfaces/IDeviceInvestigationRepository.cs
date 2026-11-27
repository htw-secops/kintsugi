using Kintsugi.Application.AiSettings;
using Kintsugi.Domain.Entities;

namespace Kintsugi.Application.Common.Interfaces;

public interface IDeviceInvestigationRepository
{
    Task<DeviceInvestigationSettings?> GetSettingsAsync(CancellationToken cancellationToken);

    Task AddSettingsAsync(DeviceInvestigationSettings settings, CancellationToken cancellationToken);

    Task AddAsync(DeviceInvestigation investigation, CancellationToken cancellationToken);

    /// <summary>The newest investigation of one device, tracked, or null.</summary>
    Task<DeviceInvestigation?> GetLatestForHostAsync(Guid hostId, CancellationToken cancellationToken);

    /// <summary>Investigations with something left to do, tracked: every one without a report (whatever its age, so one
    /// left waiting while the feature was off is still written up), and those opened since <paramref name="alertSinceUtc"/>
    /// whose alert is due.</summary>
    Task<IReadOnlyList<DeviceInvestigation>> GetUnfinishedAsync(DateTimeOffset alertSinceUtc, CancellationToken cancellationToken);
}

/// <summary>The one AI call an automatic investigation makes. Implemented on <c>AiUpgradePathResearchClient</c>, so it
/// shares the per-provider dispatch, the budget and the usage ledger.</summary>
public interface IDeviceInvestigationClient
{
    /// <summary>The model's answer as written. Throws if the provider call failed or the budget is spent.</summary>
    Task<string> WriteInvestigationAsync(AiProviderSettings settings, string prompt, CancellationToken cancellationToken);
}

/// <summary>Posts one message to a Slack (or Google Chat) incoming webhook. Throws when it was not accepted.</summary>
public interface IChatWebhookClient
{
    Task PostAsync(string webhookUrl, string text, CancellationToken cancellationToken);
}
