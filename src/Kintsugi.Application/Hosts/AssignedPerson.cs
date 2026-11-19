namespace Kintsugi.Application.Hosts;

/// <summary>What the Hosts list may show of the person a device is assigned to, and nothing more.</summary>
public static class AssignedPerson
{
    /// <summary>
    /// The first name and the initial of the last one ("Alex Morgan" becomes "Alex M."). **Computed here, on the
    /// server, so the full name never travels in the Hosts list**: reading it takes a Reveal, which is recorded with
    /// the administrator's name. A single word is returned as it is, and blank is null.
    /// </summary>
    public static string? ShortName(string? fullName)
    {
        var parts = (fullName ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return parts.Length switch
        {
            0 => null,
            1 => parts[0],
            _ => $"{parts[0]} {char.ToUpperInvariant(parts[^1][0])}.",
        };
    }
}
