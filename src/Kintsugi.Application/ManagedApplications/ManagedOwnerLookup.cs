using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace Kintsugi.Application.ManagedApplications;

/// <summary>
/// Application name to the tool that patches it, for the applications Kintsugi steps back from.
/// A name that contains <c>*</c> is a pattern (<c>Microsoft.VCLibs.*</c>, <c>CrowdStrike*</c>):
/// <c>*</c> stands for any run of characters, everything else is literal, and the match ignores
/// case like every other name comparison here.
/// </summary>
/// <remarks>
/// It is a read-only dictionary so the callers that already ask "is this name managed, and by
/// whom?" (<c>ContainsKey</c>, <c>TryGetValue</c>, <c>GetValueOrDefault</c>) keep working and get
/// patterns for free. An exact name wins over a pattern, so a person can give one application its
/// own owner without changing the pattern it also matches.
/// </remarks>
public sealed class ManagedOwnerLookup : IReadOnlyDictionary<string, string>
{
    private readonly Dictionary<string, string> _exact = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<(string Name, Regex Pattern, string Owner)> _patterns = new();

    public ManagedOwnerLookup(IEnumerable<KeyValuePair<string, string>> entries)
    {
        foreach (var (name, owner) in entries)
        {
            if (name.Contains('*'))
            {
                var regex = "^" + string.Join(".*", name.Split('*').Select(Regex.Escape)) + "$";
                _patterns.Add((name, new Regex(regex, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)), owner));
            }
            else
            {
                _exact[name] = owner;
            }
        }
    }

    public string this[string key] => TryGetValue(key, out var owner) ? owner : throw new KeyNotFoundException(key);

    public IEnumerable<string> Keys => _exact.Keys.Concat(_patterns.Select(p => p.Name));

    public IEnumerable<string> Values => _exact.Values.Concat(_patterns.Select(p => p.Owner));

    public int Count => _exact.Count + _patterns.Count;

    public bool ContainsKey(string key) => TryGetValue(key, out _);

    public bool TryGetValue(string key, [MaybeNullWhen(false)] out string value)
    {
        if (_exact.TryGetValue(key, out value))
        {
            return true;
        }

        foreach (var (_, pattern, owner) in _patterns)
        {
            if (pattern.IsMatch(key))
            {
                value = owner;
                return true;
            }
        }

        value = null;
        return false;
    }

    public IEnumerator<KeyValuePair<string, string>> GetEnumerator() =>
        _exact.Concat(_patterns.Select(p => new KeyValuePair<string, string>(p.Name, p.Owner))).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
