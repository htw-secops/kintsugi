using System.Text.RegularExpressions;
using Kintsugi.Domain.Entities;

namespace Kintsugi.Application.ManagedApplications;

/// <summary>
/// Finds the managed-application row that covers an application name: the row for exactly that name, else the most
/// specific pattern row that matches it. The rows, not just the owners, because a caller may have to say which row
/// it is (an application covered by <c>CrowdStrike*</c> is stopped being managed by editing that pattern, not by
/// removing a row named after the application).
/// </summary>
/// <remarks>Build one per request and call <see cref="Find(string)"/> for every name: each pattern is compiled once
/// here, not once per name, which is what a screen that polls and asks for thousands of names needs.</remarks>
public sealed class ManagedApplicationMatcher
{
    private readonly Dictionary<string, ManagedApplication> _exact = new();
    private readonly List<(ManagedApplication Row, Regex Pattern)> _patterns = new();

    public ManagedApplicationMatcher(IEnumerable<ManagedApplication> rows)
    {
        foreach (var row in rows)
        {
            _exact.TryAdd(row.NormalizedName, row);
            if (row.ApplicationName.Contains('*'))
            {
                _patterns.Add((row, ManagedOwnerLookup.ToRegex(row.ApplicationName)));
            }
        }
    }

    public ManagedApplication? Find(string applicationName)
    {
        if (_exact.TryGetValue(ManagedApplication.Normalize(applicationName), out var exact))
        {
            return exact; // an exact name always wins over a pattern
        }

        var name = applicationName.Trim();
        ManagedApplication? best = null;
        foreach (var (row, pattern) in _patterns)
        {
            if (pattern.IsMatch(name) && (best is null || ManagedOwnerLookup.CompareSpecificity(row.ApplicationName, best.ApplicationName) < 0))
            {
                best = row;
            }
        }

        return best;
    }

    /// <summary>One-off lookup. A caller that asks for many names builds a matcher instead.</summary>
    public static ManagedApplication? Find(IEnumerable<ManagedApplication> rows, string applicationName) =>
        new ManagedApplicationMatcher(rows).Find(applicationName);
}
