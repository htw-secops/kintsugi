namespace Kintsugi.Application.Hosts;

/// <summary>
/// The three-colour summary the Hosts page draws for a device: <b>Problem</b> (red) when a patch
/// has failed and is still outstanding, <b>Pending</b> (amber) when nothing has failed but its agent is behind or it
/// has not been seen for days or is Windows and out of patch compliance (see <see cref="WindowsPatchCompliance"/>),
/// and <b>Healthy</b> (green) otherwise. One rule, here, so the page, the bars and the
/// counts cannot disagree about what a colour means.
///
/// An unstable device (see <c>HostHealthAssessor</c>) is <b>not</b> a colour here: it is a separate flag on the host,
/// drawn as a badge, because it says why patching may fail (a shutdown or a lost connection in the middle of a
/// patch), not that a patch needs a fix. The Autonomy page is where that is read.
/// </summary>
public static class HostStanding
{
    public const string Healthy = "Healthy";
    public const string Pending = "Pending";
    public const string Problem = "Problem";

    /// <summary>How long a host may go unseen before that alone makes it Pending. Laptops are shut at weekends, so a
    /// shorter window would turn most of the fleet amber every Monday.</summary>
    public const int NotSeenDays = 3;

    /// <returns>The state and, when it is not Healthy, a short plain reason ("2 failed patches").</returns>
    public static (string State, string? Reason) Of(
        int outstandingFailedPatches,
        AgentUpdateState agent,
        DateTimeOffset? lastSeenUtc,
        DateTimeOffset now,
        (string State, string Reason)? patchCompliance = null)
    {
        var problems = new List<string>();
        if (outstandingFailedPatches > 0)
        {
            problems.Add($"{outstandingFailedPatches} failed patch{(outstandingFailedPatches == 1 ? string.Empty : "es")}");
        }

        if (problems.Count > 0)
        {
            return (Problem, string.Join(", ", problems));
        }

        var pending = new List<string>();
        if (agent == AgentUpdateState.NotUpdating)
        {
            pending.Add("Agent not updating");
        }
        else if (agent == AgentUpdateState.Behind)
        {
            pending.Add("Agent behind");
        }

        // Only a host out of compliance, not one still inside its grace period (Due) or one not heard from
        // (Stale, which the not-seen rule below already makes amber).
        if (patchCompliance is { State: WindowsPatchCompliance.NonCompliant })
        {
            pending.Add(patchCompliance.Value.Reason);
        }

        if (lastSeenUtc is null)
        {
            pending.Add("Never seen");
        }
        else if (now - lastSeenUtc.Value >= TimeSpan.FromDays(NotSeenDays))
        {
            var days = (int)(now - lastSeenUtc.Value).TotalDays;
            pending.Add($"Not seen for {days} days");
        }

        return pending.Count > 0 ? (Pending, string.Join(", ", pending)) : (Healthy, null);
    }
}
