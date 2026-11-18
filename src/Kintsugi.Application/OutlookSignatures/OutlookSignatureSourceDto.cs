namespace Kintsugi.Application.OutlookSignatures;

/// <summary>
/// The signature source settings as the settings page sees them, with how the last pull went. Every field
/// is returned: nothing here is a credential (see <c>OutlookSignatureSourceSettings</c>). Read by the admin
/// UI's <c>web/lib/data/models/</c> mapper, which mirrors it by hand.
/// </summary>
/// <param name="IsConfigured">Whether a pull could run if it were enabled.</param>
/// <param name="CopyCount">How many signature copies this server holds right now.</param>
public record OutlookSignatureSourceDto(
    bool Enabled,
    string? EndpointUrl,
    string? Audience,
    string WindowsUsernameSuffix,
    bool IsConfigured,
    DateTimeOffset? LastPullUtc,
    string? LastPullStatus,
    int? LastPullCount,
    string? LastError,
    int CopyCount);
