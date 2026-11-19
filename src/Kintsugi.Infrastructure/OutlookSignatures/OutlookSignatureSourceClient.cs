using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Kintsugi.Application.Common.Exceptions;
using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Application.OutlookSignatures;
using Kintsugi.Domain.Entities;

namespace Kintsugi.Infrastructure.OutlookSignatures;

/// <summary>
/// Fetches the signature source's bulk read with an ID token for this server's own identity.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The token goes to the configured https address and nowhere else.</strong> Three things hold
/// that: the URL is checked for https here, before a token is even minted (the settings entity checks it
/// too, but a row could be written some other way); redirects are not followed — the primary handler is
/// registered with <c>AllowAutoRedirect = false</c> in <c>DependencyInjection</c>, so a 3xx is an error
/// rather than a second request; and header values are redacted from the HTTP client's own logging. The
/// token is never put into a message, so nothing this throws can carry it into <c>LastError</c>.
/// </para>
/// <para>
/// The body is read up to <see cref="MaxResponseBytes"/> and no further. That is an extra cap beside the
/// pull's own per-entry ones, and it is what keeps a broken source from making this server buffer an
/// unbounded response before those checks ever run.
/// </para>
/// </remarks>
public class OutlookSignatureSourceClient : IOutlookSignatureSourceClient
{
    /// <summary>64 MiB: hundreds of times a real organisation's signatures, and a bound on memory.</summary>
    public const long MaxResponseBytes = 64L * 1024 * 1024;

    /// <summary>How long the body may take once the headers have arrived. Settable only so a test need not
    /// wait a minute; nothing else changes it.</summary>
    public TimeSpan BodyTimeout { get; init; } = TimeSpan.FromSeconds(60);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly IGoogleIdTokenProvider _tokens;

    public OutlookSignatureSourceClient(HttpClient http, IGoogleIdTokenProvider tokens)
    {
        _http = http;
        _tokens = tokens;
    }

    public async Task<OutlookSignatureSourceResponse> FetchAsync(string endpointUrl, CancellationToken cancellationToken)
    {
        if (!OutlookSignatureSourceSettings.IsHttpsUrl(endpointUrl))
        {
            throw new ExternalServiceException("The endpoint URL is not an absolute https:// URL, so no token was sent.");
        }

        // The audience is the address the token is about to be sent to, byte for byte — so a token minted here
        // is good at that address only, and no setting can ask for a token meant for anything else.
        var token = await _tokens.GetAsync(endpointUrl, cancellationToken);

        using var request = new HttpRequestMessage(HttpMethod.Get, endpointUrl);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            throw new ExternalServiceException($"Could not reach the signature source: {ex.Message}", ex);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ExternalServiceException("The signature source did not answer in time.", ex);
        }

        using (response)
        {
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                throw new ExternalServiceException(
                    $"The signature source refused this server's identity (HTTP {(int)response.StatusCode}). The token's audience is "
                    + "the endpoint URL exactly as saved: check the source expects that string, and lists this server's service account as a reader.");
            }

            if ((int)response.StatusCode is >= 300 and < 400)
            {
                throw new ExternalServiceException(
                    $"The signature source answered with a redirect (HTTP {(int)response.StatusCode}). Redirects are not followed, "
                    + "so the token only ever goes to the configured address: set the endpoint URL to the final address.");
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new ExternalServiceException($"The signature source answered HTTP {(int)response.StatusCode}.");
            }

            if (response.Content.Headers.ContentLength > MaxResponseBytes)
            {
                throw new ExternalServiceException($"The signature source's response is over {MaxResponseBytes / (1024 * 1024)} MiB.");
            }

            // HttpClient.Timeout stops at the headers when ResponseHeadersRead is used, so the body gets a
            // deadline of its own. Without it a source that sends headers and then stalls would hold this pull
            // — and every later hourly one — for ever, with nothing recorded.
            using var bodyDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            bodyDeadline.CancelAfter(BodyTimeout);
            try
            {
                await using var body = await response.Content.ReadAsStreamAsync(bodyDeadline.Token);
                using var buffer = new MemoryStream();
                await CopyCappedAsync(body, buffer, bodyDeadline.Token);
                buffer.Position = 0;

                return await JsonSerializer.DeserializeAsync<OutlookSignatureSourceResponse>(buffer, JsonOptions, cancellationToken)
                    ?? throw new ExternalServiceException("The signature source's response was empty.");
            }
            catch (JsonException)
            {
                // The parser's message can quote the body; the body is a person's signature. Say only where.
                throw new ExternalServiceException("The signature source's response is not the expected JSON.");
            }
            catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                throw new ExternalServiceException(
                    $"The signature source did not finish its response within {BodyTimeout.TotalSeconds:0} seconds.", ex);
            }
            catch (IOException ex)
            {
                throw new ExternalServiceException($"Reading the signature source's response failed: {ex.Message}", ex);
            }
        }
    }

    private static async Task CopyCappedAsync(Stream source, Stream destination, CancellationToken cancellationToken)
    {
        var chunk = new byte[81920];
        long total = 0;
        int read;
        while ((read = await source.ReadAsync(chunk, cancellationToken)) > 0)
        {
            total += read;
            if (total > MaxResponseBytes)
            {
                throw new ExternalServiceException($"The signature source's response is over {MaxResponseBytes / (1024 * 1024)} MiB.");
            }

            await destination.WriteAsync(chunk.AsMemory(0, read), cancellationToken);
        }
    }
}
