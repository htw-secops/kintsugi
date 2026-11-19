using System.Text.RegularExpressions;
using Kintsugi.Domain.Common;
using Kintsugi.Domain.Exceptions;

namespace Kintsugi.Domain.Entities;

/// <summary>
/// A value a software title's script needs and that should not be written into the script — a
/// CrowdStrike CID, a Huntress account key. Handed to the agent on the action rows of the titles
/// that name it, and to the script as the environment variable <c>KINTSUGI_SECRET_&lt;NAME&gt;</c>.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The value is a plaintext column</strong>, on the house rule that the database is not a
/// secret store (see <c>GitHubSettings</c> and <c>VantaSettings</c>, which say the same). What keeps a
/// database edit from swapping one value for another is <see cref="ValueSignature"/>, made over
/// <see cref="CanonicalText"/> whenever an administrator sets the value: every agent verifies it with
/// the pinned key and refuses the whole row if it does not match.
/// </para>
/// <para>
/// <strong>Write-only.</strong> The admin API returns a secret's name, who set it and when, never its
/// value; only agents receive the value, and only on an action row.
/// </para>
/// </remarks>
public partial class ScriptSecret : BaseEntity
{
    public const int MaxNameLength = 64;
    public const int MaxValueLength = 4096;
    public const string NameRule = "A secret name is an upper-case letter followed by up to 63 upper-case letters, digits or underscores.";

    public string Name { get; private set; } = default!;

    public string Value { get; private set; } = default!;

    public string ValueSignature { get; private set; } = default!;

    public string? UpdatedBy { get; private set; }

    public DateTimeOffset UpdatedUtc { get; private set; }

    private ScriptSecret()
    {
    }

    public static bool IsValidName(string? name) => name is not null && NamePattern().IsMatch(name);

    /// <summary>The text <see cref="ValueSignature"/> covers. Fixed by the IP-879 contract and pinned by
    /// test vectors here and in each agent.</summary>
    public static string CanonicalTextFor(string name, string value) => "kintsugi-secret-v1\n" + name + "\n" + value;

    public string CanonicalText() => CanonicalTextFor(Name, Value);

    /// <param name="signature">The server's signature over <see cref="CanonicalTextFor"/> of this name and
    /// value — computed by the caller, which holds the signing service.</param>
    public static ScriptSecret Create(string name, string value, string signature, string? updatedBy)
    {
        if (!IsValidName(name))
        {
            throw new DomainException(NameRule);
        }

        var secret = new ScriptSecret { Name = name };
        secret.Set(value, signature, updatedBy);
        return secret;
    }

    public void Set(string value, string signature, string? updatedBy)
    {
        if (string.IsNullOrEmpty(value))
        {
            throw new DomainException("A secret needs a value.");
        }

        if (value.Length > MaxValueLength)
        {
            throw new DomainException($"A secret value can be at most {MaxValueLength} characters.");
        }

        if (string.IsNullOrEmpty(signature))
        {
            throw new DomainException("A secret value must be signed when it is set.");
        }

        Value = value;
        ValueSignature = signature;
        UpdatedBy = updatedBy;
        UpdatedUtc = DateTimeOffset.UtcNow;
        MarkUpdated();
    }

    // \z rather than $: .NET's $ also matches before a trailing newline, and a name ending in one
    // would put a line break into the canonical text and the environment variable's name.
    [GeneratedRegex(@"^[A-Z][A-Z0-9_]{0,63}\z")]
    private static partial Regex NamePattern();
}
