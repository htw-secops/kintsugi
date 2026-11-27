using Kintsugi.Application.UpgradePaths;
using Kintsugi.Domain.Entities;

namespace Kintsugi.Application.Hosts;

/// <summary>
/// Whether a Windows host has the monthly cumulative updates it should, judged from the build and update
/// revision the agent already sends (<c>Host.OperatingSystemVersionId</c>, <c>"26200.9457"</c>) against a
/// <see cref="WindowsPatchBaseline"/>. One rule, here, so the Hosts list and anything later cannot disagree.
/// </summary>
/// <remarks>
/// Returns no verdict (null) rather than a guess for a host that is not Windows, or reports no revision, or
/// is on a build newer than the oldest baseline but with no row of its own: "unknown" must not read as
/// "compliant". A host not heard from for <see cref="StaleDays"/> is <see cref="Stale"/> next, because its
/// last report says nothing about today.
/// <para>
/// A Windows client build older than every baseline row (Windows 10 19045, 23H2 22631) is
/// <see cref="NonCompliant"/>: Microsoft no longer services it, so it is behind however recent its revision. Windows
/// Server is left out of that rule, because its builds are older than the client ones and still serviced.
/// </para>
/// <para>
/// A baseline only vouches for a host while it is current (<see cref="BaselineCurrentDays"/> after Microsoft's
/// release): after that a host at or above the old minimum gets no verdict rather than <see cref="Compliant"/>,
/// since a newer update may exist that the table does not know about. A host below the old minimum is behind either
/// way, so it keeps its due/non-compliant verdict. The table is only changed by a migration (see src/CLAUDE.md).
/// </para>
/// </remarks>
public static class WindowsPatchCompliance
{
    public const string Compliant = "COMPLIANT";
    public const string Due = "DUE";
    public const string NonCompliant = "NON_COMPLIANT";
    public const string Stale = "STALE";

    public const int StaleDays = 7;

    /// <summary>How long after Microsoft's release a baseline still counts as the latest. A month, plus a few days'
    /// slack for the next Patch Tuesday to be entered.</summary>
    public const int BaselineCurrentDays = 35;

    public static (string State, string Reason)? Of(
        string? operatingSystem,
        string? operatingSystemVersionId,
        DateTimeOffset? lastSeenUtc,
        IReadOnlyList<WindowsPatchBaseline> baselines,
        DateTimeOffset now)
    {
        if (PlatformBucket.From(operatingSystem) != PlatformBucket.Windows)
        {
            return null;
        }

        // Before the stale check: a host that sends no revision is unjudged, not "stale".
        if (!TryParse(operatingSystemVersionId, out var build, out var ubr))
        {
            return null;
        }

        if (lastSeenUtc is null || now - lastSeenUtc.Value >= TimeSpan.FromDays(StaleDays))
        {
            return (Stale, $"No check-in for {StaleDays} days or more");
        }

        var reached = $"{build}.{ubr}";
        var baseline = baselines.FirstOrDefault(b => b.Build == build);
        if (baseline is null)
        {
            var oldest = baselines.Select(b => int.TryParse(b.Build, out var n) ? n : int.MaxValue).DefaultIfEmpty(0).Min();
            return int.TryParse(build, out var number) && number < oldest && !operatingSystem!.Contains("Server", StringComparison.OrdinalIgnoreCase)
                ? (NonCompliant, $"Windows {reached} is a build that is no longer serviced")
                : null;
        }

        if (ubr >= baseline.MinUbr)
        {
            return now < baseline.EffectiveFromUtc.AddDays(BaselineCurrentDays)
                ? (Compliant, $"Windows {reached} is at or above {build}.{baseline.MinUbr}")
                : null;
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
