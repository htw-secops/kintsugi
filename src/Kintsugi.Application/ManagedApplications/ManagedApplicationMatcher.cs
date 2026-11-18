using Kintsugi.Domain.Entities;

namespace Kintsugi.Application.ManagedApplications;

/// <summary>
/// Finds the managed-application row that covers an application name: the row for exactly that name, else the most
/// specific pattern row that matches it. The rows, not just the owners, because a caller may have to say which row
/// it is (an application covered by <c>CrowdStrike*</c> is stopped being managed by editing that pattern, not by
/// removing a row named after the application).
/// </summary>
public static class ManagedApplicationMatcher
{
    public static ManagedApplication? Find(IEnumerable<ManagedApplication> rows, string applicationName)
    {
        var normalized = ManagedApplication.Normalize(applicationName);
        ManagedApplication? best = null;
        foreach (var row in rows)
        {
            if (row.NormalizedName == normalized)
            {
                return row; // an exact name always wins over a pattern
            }

            if (!row.ApplicationName.Contains('*') || !ManagedOwnerLookup.Matches(row.ApplicationName, applicationName.Trim()))
            {
                continue;
            }

            if (best is null || ManagedOwnerLookup.CompareSpecificity(row.ApplicationName, best.ApplicationName) < 0)
            {
                best = row;
            }
        }

        return best;
    }
}
