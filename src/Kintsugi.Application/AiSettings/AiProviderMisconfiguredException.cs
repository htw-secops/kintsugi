using Kintsugi.Application.Common.Exceptions;

namespace Kintsugi.Application.AiSettings;

/// <summary>Why a provider refused a request in a way no retry or other application will fix.</summary>
public enum AiProviderMisconfigurationKind
{
    /// <summary>HTTP 404: the model id (or the endpoint) does not exist for this connection.</summary>
    ModelNotFound,

    /// <summary>An organisation policy blocks something the request uses (HTTP 400/403 naming a
    /// <c>constraints/…</c> policy). Typically hosted web search on Vertex AI.</summary>
    PolicyViolation,

    /// <summary>HTTP 401/403: the credential is missing, expired or has no access.</summary>
    AuthFailed,
}

/// <summary>
/// The AI provider is set up wrongly, as opposed to being briefly unavailable. Its message is the one
/// sentence an operator reads — "AI provider misconfigured: …" — instead of a raw HTTP dump per
/// application, and a scan stops asking the provider once one of these has been seen (see
/// <c>AiProviderMemory</c>).
/// </summary>
public class AiProviderMisconfiguredException : ExternalServiceException
{
    public const string Prefix = "AI provider misconfigured: ";

    public AiProviderMisconfiguredException(
        AiProviderMisconfigurationKind kind, string connectionName, string? model, string detail, bool rejectsHostedWebSearch = false, string? providerDetail = null)
        : base(Prefix + detail)
    {
        Kind = kind;
        ConnectionName = connectionName;
        Model = model;
        Detail = detail;
        RejectsHostedWebSearch = rejectsHostedWebSearch;
        ProviderDetail = providerDetail;
    }

    public AiProviderMisconfigurationKind Kind { get; }

    public string ConnectionName { get; }

    public string? Model { get; }

    /// <summary>The sentence after the prefix.</summary>
    public string Detail { get; }

    /// <summary>The provider turned the request down because it carried the hosted web search tool
    /// (a policy naming <c>web_search</c> or <c>allowedPartnerModelFeatures</c>). Retrying the same
    /// request without that tool can succeed.</summary>
    public bool RejectsHostedWebSearch { get; }

    /// <summary>The provider's own words, truncated, for logs. Never part of <see cref="Exception.Message"/>.</summary>
    public string? ProviderDetail { get; }

    /// <summary>The same finding as a fresh exception, for a caller refused without a request.</summary>
    public AiProviderMisconfiguredException Again() => new(Kind, ConnectionName, Model, Detail, RejectsHostedWebSearch, ProviderDetail);
}
