using Kintsugi.Domain.Entities;

namespace Kintsugi.Application.Hosts;

/// <summary>
/// Whether a Windows host has the monthly cumulative updates it should, judged from the build and update
/// revision the agent already sends (<c>Host.OperatingSystemVersionId</c>, <c>"26200.9457"</c>) against a
/// <see cref="WindowsPatchBaseline"/>. One rule, here, so the Hosts list and anything later cannot disagree.
/// </summary>
/// <remarks>
/// Returns no verdict (null) rather than a guess for a host that is not Windows, or reports no revision, or
/// is on a build with no baseline: "unknown" must not read as "compliant". A host not heard from for
/// <see cref="StaleDays"/> is <see cref="Stale"/> first, because its last report says nothing about today.
/// </remarks>
public static class WindowsPatchCompliance
{
    public const string Compliant = "COMPLIANT";
    public const string Due = "DUE";
    public const string NonCompliant = "NON_COMPLIANT";
    public const string Stale = "STALE";

    public const int StaleDays = 7;

    public static (string State, string Reason)? Of(
        string? operatingSystem,
        string? operatingSystemVersionId,
        DateTimeOffset? lastSeenUtc,
        IReadOnlyList<WindowsPatchBaseline> baselines,
        DateTimeOffset now)
    {
        if (operatingSystem is null || !operatingSystem.Contains("Windows", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (lastSeenUtc is null || now - lastSeenUtc.Value >= TimeSpan.FromDays(StaleDays))
        {
            return (Stale, $"No check-in for {StaleDays} days or more");
        }

        if (!TryParse(operatingSystemVersionId, out var build, out var ubr))
        {
            return null;
        }

        var baseline = baselines.FirstOrDefault(b => b.Build == build);
        if (baseline is null)
        {
            return null;
        }

        var reached = $"{build}.{ubr}";
        if (ubr >= baseline.MinUbr)
        {
            return (Compliant, $"Windows {reached} is at or above {build}.{baseline.MinUbr}");
        }

        var behind = $"Windows {reached} is behind {build}.{baseline.MinUbr}";
        return now < baseline.EffectiveFromUtc.AddDays(baseline.GraceDays)
            ? (Due, behind)
            : (NonCompliant, behind);
    }

    /// <summary>Reads <c>"26200.9457"</c> (or <c>"10.0.26200.9457"</c>); anything else is not a verdict.</summary>
    private static bool TryParse(string? versionId, out string build, out int ubr)
    {
        build = string.Empty;
        ubr = 0;
        var text = versionId?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        if (text.StartsWith("10.0.", StringComparison.Ordinal))
        {
            text = text[5..];
        }

        var parts = text.Split('.');
        if (parts.Length != 2 || parts[0].Length == 0 || !parts[0].All(char.IsAsciiDigit) || !int.TryParse(parts[1], out ubr) || ubr < 0)
        {
            return false;
        }

        build = parts[0];
        return true;
    }
}
