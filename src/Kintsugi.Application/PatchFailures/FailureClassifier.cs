namespace Kintsugi.Application.PatchFailures;

/// <summary>
/// Sorts a failing script's captured output into a short reason class, by plain text rules. Every
/// rule lives in <see cref="Rules"/> below — one table, edited in one place — so adding a pattern
/// is a one-line change and the order of the table is the order of precedence.
/// </summary>
/// <remarks>
/// Deliberately rule-based and local: it runs on every failure report, costs nothing and never
/// leaves the server. It only names a class ("looks like a network or certificate problem"); it
/// never decides that a failure is harmless — that is <see cref="PatchFailureReconciler"/>'s job,
/// and it needs inventory evidence, not wording.
/// </remarks>
public static class FailureClassifier
{
    public const string NetworkOrCertificate = "NetworkOrCertificate";
    public const string PermissionDenied = "PermissionDenied";
    public const string DiskSpace = "DiskSpace";
    public const string FileInUse = "FileInUse";
    public const string PackageSourceUnavailable = "PackageSourceUnavailable";
    public const string NoApplicableUpdate = "NoApplicableUpdate";
    public const string ScriptError = "ScriptError";

    /// <summary>(category, case-insensitive fragments). First matching row wins.</summary>
    private static readonly (string Category, string[] Fragments)[] Rules =
    {
        (NoApplicableUpdate, new[] { "no applicable update", "no newer package versions", "no available upgrade", "already installed", "is up to date", "no installed package found matching" }),
        (NetworkOrCertificate, new[] { "0x80072ee7", "0x80072efd", "0x80072f8f", "0x80072ee2", "ssl", "certificate", "could not resolve host", "name or service not known", "timed out", "connection refused", "network is unreachable", "unable to connect" }),
        (PermissionDenied, new[] { "access is denied", "permission denied", "0x80070005", "operation not permitted", "not permitted" }),
        (DiskSpace, new[] { "no space left", "not enough space", "insufficient disk", "0x80070070" }),
        (FileInUse, new[] { "being used by another process", "application is running", "close the application", "is currently running", "0x80070020" }),
        (PackageSourceUnavailable, new[] { "source reset", "msstore", "failed when opening source", "no packages found", "could not find package", "no such file or directory" }),
    };

    /// <summary>What the AI fallback may answer with — every rule category (not
    /// <see cref="ScriptError"/>, which is "nothing matched"). Anything else it says is discarded.</summary>
    public static readonly IReadOnlyList<string> AiCategories =
        Rules.Select(r => r.Category).Distinct().ToList();

    /// <summary>The first matching category, or <see cref="ScriptError"/> when the output says
    /// something but nothing matched, or null when there is no output to read.</summary>
    public static string? Classify(string? details)
    {
        if (string.IsNullOrWhiteSpace(details))
        {
            return null;
        }

        foreach (var (category, fragments) in Rules)
        {
            foreach (var fragment in fragments)
            {
                if (details.Contains(fragment, StringComparison.OrdinalIgnoreCase))
                {
                    return category;
                }
            }
        }

        return ScriptError;
    }
}
