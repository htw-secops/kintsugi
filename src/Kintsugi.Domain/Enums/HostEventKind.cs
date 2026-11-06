namespace Kintsugi.Domain.Enums;

/// <summary>What happened, on the host's activity timeline.</summary>
/// <remarks>
/// Stored by name, so a value can be added but never renamed. Everything here is something the
/// *server* saw; what the person at the host did (the warning shown, a Delay click) is not on this
/// list yet because no agent reports it.
/// </remarks>
public enum HostEventKind
{
    /// <summary>An administrator pressed Patch now. <c>Actor</c> is who.</summary>
    PatchNowRequested,

    /// <summary>The host's agent collected a Patch now request.</summary>
    PatchNowPickedUp,

    /// <summary>An application was patched to a new version.</summary>
    ApplicationPatched,

    /// <summary>The operating system's pending update was installed.</summary>
    OperatingSystemPatched,

    /// <summary>An install failed. Repeats of the same failure are folded into one event with a count.</summary>
    PatchFailed,

    /// <summary>An administrator cleared a failure by hand. <c>Actor</c> is who.</summary>
    FailureDismissed,
}
