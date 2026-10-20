using System.Security.Claims;

namespace Kintsugi.Application.Authentication;

/// <summary>
/// Reads who a signed-in browser user is from their claims — the one place that knows what the
/// claim is called, so the allowlist check, the signer recorded on an approval and the name the
/// admin UI shows cannot disagree.
/// </summary>
/// <remarks>
/// Tolerant of both spellings on purpose. ASP.NET Core's OpenID Connect handler renames incoming
/// claims by default (<c>email</c> becomes <see cref="ClaimTypes.Email"/>), and which spelling the
/// session cookie carries depends on that setting; production showed <c>Identity.Name</c> and
/// <c>FindFirst("email")</c> both null with the defaults (cause inferred, not reproduced). <c>DynamicOpenIdConnectOptionsConfigurator</c>
/// now keeps the provider's own names, but a cookie issued before that change still carries the
/// long ones until it expires, and reading either is cheaper than debugging a lockout.
/// </remarks>
public static class AdminIdentity
{
    /// <summary>The signed-in user's email, lower-cased, or null when there is none or the provider
    /// says it is unverified. Google sends <c>email_verified</c>; Microsoft Entra does not, so
    /// "absent" passes and only an explicit "not true" is refused.</summary>
    public static string? VerifiedEmailOf(ClaimsPrincipal? user)
    {
        if (user?.Identity?.IsAuthenticated != true)
        {
            return null;
        }

        var verified = user.FindFirst("email_verified")?.Value;
        if (verified is not null && !string.Equals(verified, "true", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var email = (user.FindFirst("email") ?? user.FindFirst(ClaimTypes.Email))?.Value;
        return string.IsNullOrWhiteSpace(email) ? null : email.Trim().ToLowerInvariant();
    }

    /// <summary>What the admin UI shows for this user: the part of their email before the
    /// <c>@</c> (<c>juan.roman</c>), falling back to the provider's display name. Display only —
    /// the allowlist and the audit trail use the full email.</summary>
    public static string? DisplayNameOf(ClaimsPrincipal? user)
    {
        var email = VerifiedEmailOf(user);
        if (email is not null)
        {
            var at = email.IndexOf('@');
            return at > 0 ? email[..at] : email;
        }

        return user?.Identity?.Name;
    }

    /// <summary>Who to record as the reviewer of a signed script: the full email, so the entry says
    /// unambiguously which person signed. Null when authentication is off. The approvals repository
    /// is private, but this is the corporate address of whoever signs — revisit if that changes.</summary>
    public static string? SignerOf(ClaimsPrincipal? user) => VerifiedEmailOf(user) ?? user?.Identity?.Name;
}
