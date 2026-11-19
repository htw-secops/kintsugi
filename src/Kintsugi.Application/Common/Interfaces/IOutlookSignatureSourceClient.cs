using Kintsugi.Application.OutlookSignatures;

namespace Kintsugi.Application.Common.Interfaces;

/// <summary>
/// The signature source's bulk read, over https with an ID token for this server's own Google identity,
/// minted with <paramref name="endpointUrl"/> itself as the audience.
/// </summary>
/// <remarks>
/// Returns the body as parsed, unchecked: the caps, the email checks and the decision what to do with it
/// are <c>PullOutlookSignaturesCommandHandler</c>'s, so they are tested without a network. Throws
/// <see cref="Common.Exceptions.ExternalServiceException"/> for everything that is not a 200 with a JSON
/// body — an unreachable host, a timeout, a 401, a redirect, a body over the size cap or one that is not
/// JSON — with a message fit for the settings page and never the token.
/// </remarks>
public interface IOutlookSignatureSourceClient
{
    /// <param name="endpointUrl">Where to send the request, and the token's audience, exactly as given:
    /// <c>OutlookSignatureSourceSettings.TokenAudience</c>. There is no way to ask for any other audience.</param>
    Task<OutlookSignatureSourceResponse> FetchAsync(string endpointUrl, CancellationToken cancellationToken);
}
