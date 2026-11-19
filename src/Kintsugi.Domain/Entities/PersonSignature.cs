using System.Security.Cryptography;
using System.Text;
using Kintsugi.Domain.Common;
using Kintsugi.Domain.Exceptions;

namespace Kintsugi.Domain.Entities;

/// <summary>
/// This server's copy of one person's rendered Outlook signature, as the signature source last served it
/// (see <see cref="OutlookSignatureSourceSettings"/>). Agents are handed it for the people signed in on
/// their device; it is never rendered or edited here.
/// </summary>
/// <remarks>
/// <para>
/// <strong>One row per email, lower-cased.</strong> The source is the only renderer, so a copy is replaced
/// whole on each pull rather than merged, and a person the source stops returning loses their row.
/// </para>
/// <para>
/// <see cref="Sha256"/> is computed here over the UTF-8 bytes of <see cref="Html"/>, never taken from the
/// source: it is what an agent compares against what it last wrote, so it has to describe the bytes this
/// server will actually serve.
/// </para>
/// </remarks>
public class PersonSignature : BaseEntity
{
    /// <summary>254 is the longest address SMTP permits.</summary>
    public const int MaxEmailLength = 254;

    /// <summary>The most either body may be, in UTF-8 bytes: 256 KiB, far above any real signature. The
    /// pull refuses a whole response with one copy over it rather than storing part of it.</summary>
    public const int MaxBodyBytes = 256 * 1024;

    public string Email { get; private set; } = default!;

    public string Html { get; private set; } = default!;

    public string Text { get; private set; } = default!;

    /// <summary>Lower-case hex SHA-256 of <see cref="Html"/>'s UTF-8 bytes.</summary>
    public string Sha256 { get; private set; } = default!;

    /// <summary>When the source rendered this copy, as the source reported it.</summary>
    public DateTimeOffset RenderedAtUtc { get; private set; }

    /// <summary>When this server last received it — moves on every pull that still lists the person, even
    /// when nothing in the copy changed.</summary>
    public DateTimeOffset PulledAtUtc { get; private set; }

    private PersonSignature()
    {
    }

    public static PersonSignature Create(string email, string html, string text, DateTimeOffset renderedAtUtc, DateTimeOffset pulledAtUtc)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            throw new DomainException("A signature copy must name the person's email.");
        }

        var signature = new PersonSignature { Email = NormalizeEmail(email) };
        signature.Replace(html, text, renderedAtUtc, pulledAtUtc);
        return signature;
    }

    public void Replace(string html, string text, DateTimeOffset renderedAtUtc, DateTimeOffset pulledAtUtc)
    {
        Html = html ?? throw new DomainException("A signature copy needs its HTML.");
        Text = text ?? throw new DomainException("A signature copy needs its text.");
        Sha256 = HashOf(html);
        RenderedAtUtc = renderedAtUtc.ToUniversalTime();
        PulledAtUtc = pulledAtUtc;
        MarkUpdated();
    }

    /// <summary>The one spelling of an email this server stores and compares: trimmed and lower-case.</summary>
    public static string NormalizeEmail(string email) => email.Trim().ToLowerInvariant();

    public static string HashOf(string html) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(html))).ToLowerInvariant();
}
