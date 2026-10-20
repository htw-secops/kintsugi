using System.Security.Claims;
using Kintsugi.Application.Authentication;
using Kintsugi.Domain.Entities;
using Kintsugi.Domain.Enums;
using Kintsugi.WebApi.Filters;

namespace Kintsugi.Tests.Security;

/// <summary>
/// The administrators allowlist: who counts as an administrator once sign-in is required, and how
/// a user's email is read from the claims. The empty-list case is the one that matters most — it is
/// what makes deploying the feature unable to lock anybody out.
/// </summary>
public class AdminAllowlistTests
{
    private static AuthenticationSettings Settings(bool enabled = true, params string[] admins) =>
        AuthenticationSettings.Create(AuthProvider.GoogleWorkspace, "client-id", "secret", null, null, null, enabled, admins);

    private static ClaimsPrincipal SignedIn(params Claim[] claims) => new(new ClaimsIdentity(claims, "TestAuth"));

    private static ClaimsPrincipal Anonymous() => new(new ClaimsIdentity());

    [Fact]
    public void EmptyList_EveryoneSignedInIsAdministrator()
    {
        Assert.True(RequireAdminSessionAttribute.IsAdministrator(Settings(), SignedIn(new Claim("email", "anyone@example.invalid"))));
    }

    [Fact]
    public void EmptyList_StillRequiresSignIn()
    {
        Assert.False(RequireAdminSessionAttribute.IsAdministrator(Settings(), Anonymous()));
    }

    [Fact]
    public void SignInDisabled_AllowsEveryoneRegardlessOfList()
    {
        Assert.True(RequireAdminSessionAttribute.IsAdministrator(Settings(false, "a@example.invalid"), Anonymous()));
    }

    [Fact]
    public void ListedEmail_IsAdministrator_CaseInsensitively()
    {
        var settings = Settings(true, "Juan.Roman@Example.Invalid");
        Assert.True(RequireAdminSessionAttribute.IsAdministrator(settings, SignedIn(new Claim("email", "JUAN.roman@example.invalid"))));
    }

    [Fact]
    public void UnlistedEmail_IsNotAdministrator()
    {
        var settings = Settings(true, "admin@example.invalid");
        Assert.False(RequireAdminSessionAttribute.IsAdministrator(settings, SignedIn(new Claim("email", "other@example.invalid"))));
    }

    [Fact]
    public void ListedButUnverifiedEmail_IsNotAdministrator()
    {
        var settings = Settings(true, "admin@example.invalid");
        var user = SignedIn(new Claim("email", "admin@example.invalid"), new Claim("email_verified", "false"));
        Assert.False(RequireAdminSessionAttribute.IsAdministrator(settings, user));
    }

    [Fact]
    public void ProviderWithoutEmailVerifiedClaim_IsAccepted()
    {
        var settings = Settings(true, "admin@example.invalid");
        Assert.True(RequireAdminSessionAttribute.IsAdministrator(settings, SignedIn(new Claim("email", "admin@example.invalid"))));
    }

    [Fact]
    public void SignedInWithNoEmail_IsNotAdministratorWhenListIsSet()
    {
        Assert.False(RequireAdminSessionAttribute.IsAdministrator(Settings(true, "admin@example.invalid"), SignedIn(new Claim("name", "No Email"))));
    }

    [Fact]
    public void Email_IsReadFromEitherClaimSpelling()
    {
        Assert.Equal("a@example.invalid", AdminIdentity.VerifiedEmailOf(SignedIn(new Claim(ClaimTypes.Email, "A@Example.invalid"))));
        Assert.Equal("a@example.invalid", AdminIdentity.VerifiedEmailOf(SignedIn(new Claim("email", "a@example.invalid"))));
    }

    [Fact]
    public void DisplayName_IsTheLocalPart_AndSignerIsTheFullEmail()
    {
        var user = SignedIn(new Claim("email", "juan.roman@example.invalid"), new Claim("name", "Juan Roman"));
        Assert.Equal("juan.roman", AdminIdentity.DisplayNameOf(user));
        Assert.Equal("juan.roman@example.invalid", AdminIdentity.SignerOf(user));
    }

    [Fact]
    public void Signer_IsNullWhenNobodyIsSignedIn()
    {
        Assert.Null(AdminIdentity.SignerOf(Anonymous()));
    }

    [Fact]
    public void Update_NullKeepsTheList_EmptyClearsIt_AndEntriesAreNormalised()
    {
        var settings = Settings(true, " A@Example.invalid ", "a@example.invalid", "");
        Assert.Equal(new[] { "a@example.invalid" }, settings.AdminEmails);

        settings.Update(AuthProvider.GoogleWorkspace, "client-id", null, null, null, null, true);
        Assert.Single(settings.AdminEmails);

        settings.Update(AuthProvider.GoogleWorkspace, "client-id", null, null, null, null, true, Array.Empty<string>());
        Assert.Empty(settings.AdminEmails);
    }
}
