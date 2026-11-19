using System.Text.RegularExpressions;
using Kintsugi.Domain.Entities;

namespace Kintsugi.Application.OutlookSignatures;

/// <summary>What a signature copy, and an email naming a person, have to look like here.</summary>
public static class OutlookSignatureRules
{
    /// <summary>The most copies one pull may carry. Far above the people a signature source serves; what
    /// stops a broken or hostile source filling the table.</summary>
    public const int MaxEntries = 2000;

    /// <summary>A deliberately plain address shape: a local part of the characters real mailboxes use, and
    /// a dotted domain. It is applied after lower-casing, and it exists to refuse what is plainly not an
    /// address (a blank, a display name, a path, markup), not to implement RFC 5322.</summary>
    private static readonly Regex EmailPattern = new(
        @"^[a-z0-9.!#$%&'*+/=?^_`{|}~-]{1,64}@[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?(?:\.[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?)+$",
        RegexOptions.Compiled);

    /// <summary>Whether <paramref name="email"/>, normalised, is an address this server will key a copy on.</summary>
    public static bool IsValidEmail(string? email) =>
        email is not null
        && PersonSignature.NormalizeEmail(email) is var normalized
        && normalized.Length <= PersonSignature.MaxEmailLength
        && EmailPattern.IsMatch(normalized);
}
