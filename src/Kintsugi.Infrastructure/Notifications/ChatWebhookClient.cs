using System.Net.Http.Json;
using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Domain.Entities;

namespace Kintsugi.Infrastructure.Notifications;

/// <summary>
/// Posts <c>{"text": …}</c> to an incoming webhook. Slack and Google Chat both take exactly that body, and Google Chat
/// refuses fields it does not know, so nothing else is sent.
/// </summary>
/// <remarks>The address is a credential, so it never reaches a log or an error message: a failure says the status and
/// at most the start of the body the webhook answered with.</remarks>
public class ChatWebhookClient : IChatWebhookClient
{
    private readonly HttpClient _http;

    public ChatWebhookClient(HttpClient http)
    {
        _http = http;
    }

    public async Task PostAsync(string webhookUrl, string text, CancellationToken cancellationToken)
    {
        // Checked again here, not only on save: this is the one place the address is used.
        var url = DeviceInvestigationSettings.HttpsUrl(webhookUrl, "The webhook")!;
        using var response = await _http.PostAsJsonAsync(url, new { text }, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new HttpRequestException($"The webhook answered {(int)response.StatusCode}: {(body.Length > 200 ? body[..200] : body)}");
        }
    }
}
