using System.Collections.Concurrent;
using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Domain.Enums;

namespace Kintsugi.Application.PatchFailures;

/// <summary>
/// The in-memory memory behind <see cref="AiFailureClassifier"/>: answers already obtained, and how
/// many AI calls today has spent. A singleton, because a scoped copy would forget both on every
/// request. Lost on restart, which only costs a few repeat calls.
/// </summary>
public class FailureClassificationCache
{
    /// <summary>The most AI classifications made per UTC day, whatever fails.</summary>
    public const int DailyCap = 50;

    private readonly ConcurrentDictionary<string, string> _answers = new();
    private readonly object _gate = new();
    private DateOnly _day;
    private int _used;

    public bool TryGet(string key, out string category) => _answers.TryGetValue(key, out category!);

    public void Set(string key, string category) => _answers[key] = category;

    /// <summary>Takes one of today's calls; false when the cap is spent.</summary>
    public bool TryTake(DateTimeOffset nowUtc)
    {
        lock (_gate)
        {
            var today = DateOnly.FromDateTime(nowUtc.UtcDateTime);
            if (today != _day)
            {
                _day = today;
                _used = 0;
            }

            if (_used >= DailyCap)
            {
                return false;
            }

            _used++;
            return true;
        }
    }
}

/// <summary>
/// The fallback for failures <see cref="FailureClassifier"/>'s text rules could not name: asks the AI
/// for one category from the fixed list. It can only ever make a failure *more* specific — every
/// problem (not configured, budget stop, timeout, a nonsense answer, the daily cap) leaves the
/// category as <see cref="FailureClassifier.ScriptError"/> and never fails the failure report.
/// </summary>
public class AiFailureClassifier
{
    /// <summary>How long the report waits for the model before giving up.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    /// <summary>How much of the failing output is sent. See <see cref="FailureText.MaxChars"/>.</summary>
    public const int MaxOutputChars = FailureText.MaxChars;

    private readonly IAiProviderSettingsResolver _settings;
    private readonly IFailureClassificationClient _client;
    private readonly FailureClassificationCache _cache;

    public AiFailureClassifier(
        IAiProviderSettingsResolver settings, IFailureClassificationClient client, FailureClassificationCache cache)
    {
        _settings = settings;
        _client = client;
        _cache = cache;
    }

    /// <param name="hostname">This host's name and serial, only so they can be scrubbed out of the
    /// text that is sent — they are never sent themselves.</param>
    public async Task<string> ClassifyAsync(
        string applicationName, string details, string? hostname, string? serial, DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        try
        {
            var key = FailureText.AiCacheKey(applicationName, details);
            if (_cache.TryGet(key, out var known))
            {
                return known;
            }

            var provider = await _settings.ResolveAsync(cancellationToken);
            // The two agent providers drive a tool-using agent (shell, web); failure text is untrusted
            // third-party output, so it is never handed to one. Only plain completion routes.
            if (provider is null
                || provider.Provider is AiProvider.GooseCli or AiProvider.ClaudeAgentSdk
                || !_cache.TryTake(nowUtc))
            {
                return FailureClassifier.ScriptError;
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(Timeout);

            var output = FailureText.ScrubForAi(details, hostname, serial);
            var answer = await _client.ClassifyFailureAsync(
                provider, applicationName, output, FailureClassifier.AiCategories, timeout.Token);

            var category = Validate(answer);
            _cache.Set(key, category);
            return category;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // Not configured, over budget, timed out, provider down: the report must still go through.
            return FailureClassifier.ScriptError;
        }
    }

    /// <summary>The answer if it is exactly one of the allowed categories, otherwise
    /// <see cref="FailureClassifier.ScriptError"/> ("Unknown" lands here too).</summary>
    public static string Validate(string? answer)
    {
        var trimmed = answer?.Trim().Trim('"', '.');
        return FailureClassifier.AiCategories.FirstOrDefault(c => string.Equals(c, trimmed, StringComparison.OrdinalIgnoreCase))
            ?? FailureClassifier.ScriptError;
    }
}
