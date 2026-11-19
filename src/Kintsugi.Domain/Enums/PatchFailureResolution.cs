namespace Kintsugi.Domain.Enums;

/// <summary>
/// Whether a reported patch failure is still outstanding, and if not, what settled it. The Failed
/// Updates screen filters on this: <see cref="Outstanding"/> is what it shows by default, since a
/// row that has since patched successfully is history rather than a problem.
/// </summary>
/// <remarks>
/// No JSON converter, like <see cref="HostStatus"/> and <see cref="AiProvider"/>, so this crosses
/// the wire as an ordinal — which makes declaration order load-bearing on both ends. Append new
/// members; never insert or reorder. The mirror is <c>PatchFailureResolution</c> in
/// <c>web/lib/domain/entities/enums.dart</c>.
/// </remarks>
public enum PatchFailureResolution
{
    /// <summary>Still failing, as far as anything has told this server.</summary>
    Outstanding = 0,

    /// <summary>The same host later reported patching this application successfully — see
    /// <c>ReportPatchResultCommandHandler</c>, which closes outstanding failures as a side effect
    /// of the success report rather than leaving the screen to accumulate rows nobody can act on.</summary>
    PatchSucceeded = 1,

    /// <summary>An administrator dismissed it from the Failed Updates screen.</summary>
    Dismissed = 2,

    /// <summary>
    /// A repaired script was signed from the Failed Updates screen, so the script that produced this
    /// failure no longer exists.
    /// </summary>
    /// <remarks>
    /// Distinct from <see cref="Dismissed"/> because it is a different claim. Dismissing says "this
    /// will not happen again"; this says "the thing that failed has been replaced, and we will find
    /// out on the next patch cycle". If the repair did not work the agent reports again and
    /// <c>PatchFailure.Reopen</c> puts the row back, keeping its original count and first-failed
    /// date — so the queue is not lying in either direction while the fix is unproven.
    /// </remarks>
    ScriptRepaired = 3,

    /// <summary>
    /// The server checked the host's own inventory and the application was already at (or past)
    /// the version the failed attempt was reaching for — so the failure was a false alarm, or the
    /// application updated itself afterwards. See <c>PatchFailureReconciler</c>.
    /// </summary>
    /// <remarks>
    /// Distinct from <see cref="PatchSucceeded"/> (the agent reported a successful patch) and from
    /// <see cref="Dismissed"/> (a person decided): nobody patched anything and nobody looked, the
    /// inventory simply showed nothing was wrong. A genuine new failure reopens the row as usual.
    /// </remarks>
    FalsePositiveAutoResolved = 4
}
