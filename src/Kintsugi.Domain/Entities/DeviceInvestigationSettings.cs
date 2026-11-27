using Kintsugi.Domain.Common;
using Kintsugi.Domain.Exceptions;

namespace Kintsugi.Domain.Entities;

/// <summary>
/// Singleton: whether Kintsugi investigates an Unstable device by itself, and where it says what it found.
/// </summary>
/// <remarks>
/// <b>Off until an administrator turns it on</b>, like Vanta: it collects a device's log without anyone asking and
/// sends excerpts to the AI provider, which is a decision for each deployment. The webhook is a credential (anyone
/// holding a Slack incoming-webhook address can post to that channel), so it is write-only on the API, like every
/// other secret in settings.
/// </remarks>
public class DeviceInvestigationSettings : BaseEntity
{
    public bool Enabled { get; private set; }

    /// <summary>A Slack incoming webhook. A Google Chat space webhook takes the same <c>{"text": …}</c> post.</summary>
    public string? SlackWebhookUrl { get; private set; }

    /// <summary>The admin UI's own address, so the alert can link to the device report. Not
    /// <c>AGENT_API_BASE_URL</c>: see <c>VantaSettings.ConsoleBaseUrl</c>, which is the same thing for the same reason.</summary>
    public string? ConsoleBaseUrl { get; private set; }

    private DeviceInvestigationSettings()
    {
    }

    /// <param name="slackWebhookUrl">Null or blank keeps the stored one; the page never receives it to send back.</param>
    public static DeviceInvestigationSettings Create(bool enabled, string? slackWebhookUrl, string? consoleBaseUrl)
    {
        var settings = new DeviceInvestigationSettings();
        settings.Apply(enabled, slackWebhookUrl, consoleBaseUrl);
        return settings;
    }

    public void Update(bool enabled, string? slackWebhookUrl, string? consoleBaseUrl, bool clearSlackWebhook)
    {
        if (clearSlackWebhook)
        {
            SlackWebhookUrl = null;
        }

        Apply(enabled, slackWebhookUrl, consoleBaseUrl);
        MarkUpdated();
    }

    private void Apply(bool enabled, string? slackWebhookUrl, string? consoleBaseUrl)
    {
        Enabled = enabled;
        SlackWebhookUrl = HttpsUrl(slackWebhookUrl, "The Slack webhook") ?? SlackWebhookUrl;
        ConsoleBaseUrl = HttpsUrl(consoleBaseUrl, "This server's address")?.TrimEnd('/');
    }

    public static string? HttpsUrl(string? value, string field)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new DomainException($"{field} must be an absolute https:// URL.");
        }

        return trimmed;
    }
}
