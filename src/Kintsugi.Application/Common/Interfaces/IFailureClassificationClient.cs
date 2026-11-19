using Kintsugi.Application.AiSettings;

namespace Kintsugi.Application.Common.Interfaces;

/// <summary>
/// Asks the configured AI which one reason class a failing patch's output belongs to. Implemented
/// on <c>AiUpgradePathResearchClient</c> for the same reason <see cref="ICpeSuggestionClient"/> is:
/// to reuse its per-provider dispatch rather than grow a second copy.
/// </summary>
public interface IFailureClassificationClient
{
    /// <summary>The model's chosen category, exactly as it answered (the caller validates it against
    /// its list), or null when it declined or answered in prose. Throws only if the provider call
    /// itself failed or the budget is exhausted.</summary>
    /// <param name="applicationName">Also the usage "subject", so the cost shows against the
    /// application in AI Usage.</param>
    /// <param name="output">The failing output, already truncated. Never carries a hostname or serial.</param>
    Task<string?> ClassifyFailureAsync(
        AiProviderSettings settings, string applicationName, string output, IReadOnlyList<string> categories,
        CancellationToken cancellationToken);
}
