using System.Text.RegularExpressions;
using Kintsugi.Domain.Common;
using Kintsugi.Domain.Exceptions;

namespace Kintsugi.Domain.Entities;

/// <summary>
/// Singleton configuration for where this server pulls each person's rendered Outlook signature from,
/// and how the last pull went. The pull itself is <c>PullOutlookSignaturesCommandHandler</c>; the copies
/// it keeps are <see cref="PersonSignature"/> rows.
/// </summary>
/// <remarks>
/// <para>
/// <strong>No credential is stored here, deliberately.</strong> The source is the content service's bulk
/// signature read, and it accepts a Google-signed ID token minted for the identity this server runs as
/// (Workload Identity on GKE — see <c>GoogleIdTokenProvider</c>). So the setting is an address, and nothing
/// on this row is secret; that is why the whole row can be returned to the settings page, unlike
/// <see cref="VantaSettings"/>.
/// </para>
/// <para>
/// <strong>The token's audience is the endpoint URL, never a setting of its own</strong> (see
/// <see cref="TokenAudience"/>). A separate audience let an administrator mint a token for any other service
/// that trusts Google ID tokens, and let a token sent to one address be replayed at another. Binding the two
/// means a token is only ever minted for the address it is sent to, so the source must accept exactly that
/// string as its audience.
/// </para>
/// <para>
/// <see cref="EndpointUrl"/> must be https: the ID token travels in its <c>Authorization</c> header, and an
/// http address would put a bearer credential on the wire in the clear. The client checks again before
/// minting a token (<c>OutlookSignatureSourceClient</c>), so a row written some other way is refused there
/// too.
/// </para>
/// </remarks>
public class OutlookSignatureSourceSettings : BaseEntity
{
    public const int MaxUrlLength = 512;

    /// <summary>Windows caps a local username at 20 characters, so a suffix longer than that could never
    /// be part of one.</summary>
    public const int MaxWindowsUsernameSuffixLength = 20;

    public const int MaxErrorLength = 1000;

    public const string StatusOk = "ok";
    public const string StatusDisabled = "disabled";
    public const string StatusError = "error";

    /// <summary>Characters a Windows username suffix may hold. Narrower than what Windows permits on
    /// purpose: the suffix is appended to an email's local part to guess a username, and anything outside
    /// this set is a typo rather than a real mapping.</summary>
    private static readonly Regex SuffixPattern = new("^[A-Za-z0-9._-]*$", RegexOptions.Compiled);

    /// <summary>Whether the hourly pull runs. False leaves the copies already held in place: switching the
    /// pull off is not the kill switch — the source answering <c>disabled</c> is.</summary>
    public bool Enabled { get; private set; }

    /// <summary>The source's bulk read, an absolute https URL.</summary>
    public string? EndpointUrl { get; private set; }

    /// <summary>The <c>audience</c> the ID token is minted for: <see cref="EndpointUrl"/>, exactly as stored
    /// (trimmed, nothing else). Derived, never stored or chosen — see the class remarks.</summary>
    public string? TokenAudience => EndpointUrl;

    /// <summary>
    /// Appended to an email's local part when a signed-in Windows user has no Google Credential Provider
    /// record, to guess which person they are (the result is cut to 20 characters, Windows' cap). Empty
    /// means local part alone. Read by the per-person resolution, never by the pull.
    /// </summary>
    /// <remarks>A setting rather than a constant because it names an organisation's username convention,
    /// and this repository is public.</remarks>
    public string WindowsUsernameSuffix { get; private set; } = string.Empty;

    public DateTimeOffset? LastPullUtc { get; private set; }

    /// <summary><see cref="StatusOk"/>, <see cref="StatusDisabled"/> or <see cref="StatusError"/>; null
    /// until the first pull.</summary>
    public string? LastPullStatus { get; private set; }

    /// <summary>How many copies the last successful pull left in place: the number received for
    /// <see cref="StatusOk"/>, zero for <see cref="StatusDisabled"/>, and unchanged after an error (the
    /// copies are kept, so the count still describes them).</summary>
    public int? LastPullCount { get; private set; }

    /// <summary>
    /// How many people the last successful pull skipped, rather than refusing the whole response — today only
    /// a signature whose html or text holds U+0000, which Postgres cannot store in a text column. Zero when
    /// none. Unchanged after an error, like <see cref="LastPullCount"/>, since the copies it describes are
    /// still the ones held.
    /// </summary>
    public int LastPullSkipped { get; private set; }

    /// <summary>Why <see cref="LastPullSkipped"/> people were skipped, in words fit for the settings page;
    /// null when none were. Never names a person.</summary>
    public string? LastPullSkippedReason { get; private set; }

    /// <summary>Why the last pull failed, in words fit for the settings page. Never carries the token or
    /// a response body: only a status code and which check refused it.</summary>
    public string? LastError { get; private set; }

    private OutlookSignatureSourceSettings()
    {
    }

    public static OutlookSignatureSourceSettings Create(bool enabled, string? endpointUrl, string? windowsUsernameSuffix)
    {
        var settings = new OutlookSignatureSourceSettings();
        settings.Apply(enabled, endpointUrl, windowsUsernameSuffix);
        return settings;
    }

    public void Update(bool enabled, string? endpointUrl, string? windowsUsernameSuffix)
    {
        Apply(enabled, endpointUrl, windowsUsernameSuffix);
        MarkUpdated();
    }

    public bool IsConfigured => EndpointUrl is not null;

    /// <summary>A pull that replaced the copies (<see cref="StatusOk"/>) or removed them all
    /// (<see cref="StatusDisabled"/>).</summary>
    public void RecordPull(string status, int count, DateTimeOffset at, int skipped = 0, string? skippedReason = null)
    {
        if (skipped < 0 || (skipped > 0) != (skippedReason is not null))
        {
            throw new DomainException("A pull that skipped people says how many and why, and one that skipped none says neither.");
        }

        if (status is not (StatusOk or StatusDisabled))
        {
            throw new DomainException($"'{status}' is not a successful pull status.");
        }

        LastPullUtc = at;
        LastPullStatus = status;
        LastPullCount = count;
        LastPullSkipped = skipped;
        LastPullSkippedReason = skippedReason is null || skippedReason.Length <= MaxErrorLength ? skippedReason : skippedReason[..MaxErrorLength];
        LastError = null;
        MarkUpdated();
    }

    /// <summary>A pull that changed nothing. <see cref="LastPullCount"/> is left alone, because the copies
    /// it counts are still the ones held.</summary>
    public void RecordPullFailure(string error, DateTimeOffset at)
    {
        LastPullUtc = at;
        LastPullStatus = StatusError;
        LastError = string.IsNullOrWhiteSpace(error)
            ? "The pull failed."
            : (error.Length > MaxErrorLength ? error[..MaxErrorLength] : error);
        MarkUpdated();
    }

    /// <summary>Whether <paramref name="value"/> is an absolute https URL — the one rule both this entity
    /// and the validator apply, and that the client applies again before a token is minted.</summary>
    public static bool IsHttpsUrl(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps && string.IsNullOrEmpty(uri.UserInfo);

    public static bool IsValidSuffix(string? value) =>
        value is null || (value.Trim().Length <= MaxWindowsUsernameSuffixLength && SuffixPattern.IsMatch(value.Trim()));

    private void Apply(bool enabled, string? endpointUrl, string? windowsUsernameSuffix)
    {
        var url = Normalize(endpointUrl);
        if (url is not null && (url.Length > MaxUrlLength || !IsHttpsUrl(url)))
        {
            throw new DomainException("The endpoint URL must be an absolute https:// URL: the ID token is sent to it.");
        }

        if (!IsValidSuffix(windowsUsernameSuffix))
        {
            throw new DomainException(
                $"The Windows username suffix is at most {MaxWindowsUsernameSuffixLength} letters, digits, '.', '_' or '-'.");
        }

        EndpointUrl = url;
        WindowsUsernameSuffix = windowsUsernameSuffix?.Trim() ?? string.Empty;

        // Enabling with half a form would leave the hourly pull failing with nobody watching, so the
        // invariant lives here as well as in the validator — the same reasoning as VantaSettings.
        Enabled = enabled;
        if (Enabled && !IsConfigured)
        {
            throw new DomainException("The signature source needs an endpoint URL before it can be enabled.");
        }
    }

    private static string? Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
