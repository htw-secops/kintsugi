using Kintsugi.Application.Common.Exceptions;

namespace Kintsugi.Infrastructure.OutlookSignatures;

/// <summary>A Google-signed ID token for the identity this server runs as, minted for one audience.</summary>
public interface IGoogleIdTokenProvider
{
    Task<string> GetAsync(string audience, CancellationToken cancellationToken);
}

/// <summary>
/// An ID token from the metadata server, the identity counterpart of <c>GoogleCloudAccessTokenProvider</c>.
/// </summary>
/// <remarks>
/// <para>
/// On GKE with Workload Identity the metadata server answers as the pod's Google service account, so the
/// signature source can tell this server apart from everything else by the token's <c>email</c> claim,
/// with no secret stored on either side. <c>format=full</c> puts that claim in the token. Off Google Cloud
/// it fails, and the failure says so.
/// </para>
/// <para>
/// A singleton with a cache per audience: an ID token lasts an hour and the pull runs hourly, so the cache
/// mostly saves the start-up pull and a retry. Tokens are held for 45 minutes, comfortably inside their
/// lifetime, rather than decoding the token's own expiry — nothing here reads a token's contents.
/// The token itself is never logged, and never put into an exception message.
/// </para>
/// </remarks>
public class GoogleIdTokenProvider : IGoogleIdTokenProvider
{
    public const string IdentityUrl = "http://metadata.google.internal/computeMetadata/v1/instance/service-accounts/default/identity";

    private static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(45);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly Dictionary<string, (string Token, DateTimeOffset Until)> _cache = new(StringComparer.Ordinal);

    public GoogleIdTokenProvider(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

    public async Task<string> GetAsync(string audience, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            if (_cache.TryGetValue(audience, out var cached) && DateTimeOffset.UtcNow < cached.Until)
            {
                return cached.Token;
            }

            using var client = _httpClientFactory.CreateClient(nameof(GoogleIdTokenProvider));
            client.Timeout = TimeSpan.FromSeconds(10);
            using var request = new HttpRequestMessage(
                HttpMethod.Get, $"{IdentityUrl}?audience={Uri.EscapeDataString(audience)}&format=full");
            request.Headers.Add("Metadata-Flavor", "Google");

            HttpResponseMessage response;
            try
            {
                response = await client.SendAsync(request, cancellationToken);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
            {
                throw new ExternalServiceException(
                    "Could not reach the Google Cloud metadata server for an ID token. This only works where the server runs "
                    + "on Google Cloud (GKE with Workload Identity, a VM, Cloud Run).", ex);
            }

            using (response)
            {
                if (!response.IsSuccessStatusCode)
                {
                    throw new ExternalServiceException(
                        $"The Google Cloud metadata server refused an ID token (HTTP {(int)response.StatusCode}). On GKE, check the "
                        + "Kubernetes service account is bound to a Google service account with Workload Identity.");
                }

                var token = (await response.Content.ReadAsStringAsync(cancellationToken)).Trim();
                if (token.Length == 0)
                {
                    throw new ExternalServiceException("The Google Cloud metadata server returned an empty ID token.");
                }

                _cache[audience] = (token, DateTimeOffset.UtcNow.Add(CacheFor));
                return token;
            }
        }
        finally
        {
            _lock.Release();
        }
    }
}
