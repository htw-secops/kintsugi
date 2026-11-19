using System.Security.Cryptography;
using System.Text;
using Kintsugi.Domain.Entities;
using Kintsugi.Domain.Enums;
using Kintsugi.Domain.Exceptions;

namespace Kintsugi.Tests.Domain;

/// <summary>
/// SoftwareTitle and ScriptSecret: detection, applicability, the action, and the canonical texts every
/// agent rebuilds and verifies. The vectors here are the same strings, byte for byte, as the Rust tests
/// in each agent's <c>upgrade.rs</c> (IP-881) — change one and the fleet refuses every action row.
/// </summary>
public class SoftwareTitleTests
{
    private static readonly Guid HostId = Guid.NewGuid();

    private static SoftwareTitle Winget(SoftwareMode mode = SoftwareMode.Required, string? detectAs = null, params string[] secrets) =>
        SoftwareTitle.Create("Greenshot", "pm:winget", "winget", "Windows", "Greenshot.Greenshot", detectAs, mode, secrets, "admin@example.com");

    private static SoftwareTitle Custom(SoftwareMode mode = SoftwareMode.Required, string? detectAs = null, params string[] secrets) =>
        SoftwareTitle.Create("Zoho Assist", "Windows", null, "Windows", null, detectAs, mode, secrets, "admin@example.com");

    private static InstalledApplication App(string name, string? identifier = null) => new(HostId, name, "1.0", identifier);

    // ---- Canonical texts: the fixed vectors (copied verbatim into the agents' tests) ----

    /// <summary>Action vector 1: a package-manager install with two secrets, given out of order.</summary>
    public const string ActionVectorInstall =
        "kintsugi-action-v1\nInstall\nwinget\nGreenshot\nGreenshot.Greenshot\nTEST_SECRET_A,TEST_SECRET_B";

    /// <summary>Action vector 2: a custom uninstall with no secrets — an empty manager line, the title name
    /// as the identifier, and a trailing newline before the empty secret list.</summary>
    public const string ActionVectorUninstall =
        "kintsugi-action-v1\nUninstall\n\nZoho Assist\nZoho Assist\n";

    /// <summary>Secret vector.</summary>
    public const string SecretVector = "kintsugi-secret-v1\nTEST_SECRET_A\nTEST_SECRET_VALUE";

    /// <summary>
    /// A throwaway P-256 public key, and signatures made with its (discarded) private key over the UTF-8
    /// bytes of each vector above — ASN.1 DER, base64, exactly what <c>ArtifactSigningService.Sign</c>
    /// produces. Lets each agent's <c>identity::verify_artifact_signature</c> be tested against the same
    /// bytes without a server. Not a key any deployment uses.
    /// </summary>
    public const string VectorPublicKeyPem = """
        -----BEGIN PUBLIC KEY-----
        MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEi2WXbdjBuXiEu0Ocj0pY1DkLHpHn
        83k5xJ3uZR0m6TdatTZOoNWv+u6xYbLv3pibsOAzMhP7SWKRIxTevVVhrA==
        -----END PUBLIC KEY-----
        """;

    public const string ActionVectorInstallSignature =
        "MEUCIAOEin4HKT8ux3I4rc/8Zlf0XbJlGQbRUJoy68K2uz9kAiEAoiihP5CRKIAhsc8gvLtAv7gP+vDNWuVQHLgIgJ4Ze2w=";

    public const string ActionVectorUninstallSignature =
        "MEUCIQDpXK5YI6DMxwSFLsUqxWvjJTO1H/xn7K4leXAIGaOeHwIgBm0PvP4CzDqaMnF0AcWZhlxDNUg89sTKjIri0jDM2WA=";

    public const string SecretVectorSignature =
        "MEUCIQC4UAG/hlCMj8tWdJzxlug6HNE0a6xg7cTMEJpOjaedhgIgP4KNUjiEaCTsyQd3IiMdc2QyebtbgUPi7pRxKdPhdic=";

    [Fact]
    public void CanonicalActionText_ForAPackageManagerInstall_IsTheFixedVector_WithSecretNamesSorted()
    {
        var title = Winget(SoftwareMode.Required, null, "TEST_SECRET_B", "TEST_SECRET_A");

        Assert.Equal(ActionVectorInstall, title.CanonicalActionText());
    }

    [Fact]
    public void CanonicalActionText_ForACustomUninstall_IsTheFixedVector()
    {
        var title = Custom(SoftwareMode.Prohibited);

        Assert.Equal(ActionVectorUninstall, title.CanonicalActionText());
    }

    [Fact]
    public void CanonicalActionText_SelfServiceInstallsLikeRequired()
    {
        // The mode is not in the text, only the action: Required and Self-service both install, and the
        // agent tells them apart by the row's selfService flag, which needs no signature because it can
        // only make a row do less (offer rather than act).
        Assert.Equal(
            Winget(SoftwareMode.Required, null, "TEST_SECRET_A", "TEST_SECRET_B").CanonicalActionText(),
            Winget(SoftwareMode.SelfService, null, "TEST_SECRET_A", "TEST_SECRET_B").CanonicalActionText());
    }

    [Fact]
    public void CanonicalText_ForASecret_IsTheFixedVector()
    {
        Assert.Equal(SecretVector, ScriptSecret.CanonicalTextFor("TEST_SECRET_A", "TEST_SECRET_VALUE"));
        Assert.Equal(SecretVector, ScriptSecret.Create("TEST_SECRET_A", "TEST_SECRET_VALUE", "sig", null).CanonicalText());
    }

    [Theory]
    [InlineData(ActionVectorInstall, ActionVectorInstallSignature)]
    [InlineData(ActionVectorUninstall, ActionVectorUninstallSignature)]
    [InlineData(SecretVector, SecretVectorSignature)]
    public void VectorSignatures_VerifyOverTheVectorsUtf8Bytes_WithTheVectorKey(string text, string signature)
    {
        using var key = ECDsa.Create();
        key.ImportFromPem(VectorPublicKeyPem);

        Assert.True(key.VerifyData(
            Encoding.UTF8.GetBytes(text), Convert.FromBase64String(signature), HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence));
        // And a one-byte change does not verify, which is the property the agents rely on.
        Assert.False(key.VerifyData(
            Encoding.UTF8.GetBytes(text + "x"), Convert.FromBase64String(signature), HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence));
    }

    // ---- Action ----

    [Theory]
    [InlineData(SoftwareMode.Required, "Install")]
    [InlineData(SoftwareMode.SelfService, "Install")]
    [InlineData(SoftwareMode.Prohibited, "Uninstall")]
    public void Action_FollowsTheMode(SoftwareMode mode, string expected)
    {
        Assert.Equal(expected, Winget(mode).Action);
    }

    [Fact]
    public void ApplicationIdentifier_IsThePackageId_OrTheNameForACustomTitle()
    {
        Assert.Equal("Greenshot.Greenshot", Winget().ApplicationIdentifier);
        Assert.Equal("Zoho Assist", Custom().ApplicationIdentifier);
    }

    // ---- Detection ----

    [Fact]
    public void IsInstalledOn_MatchesThePackageIdAsTheIdentifier_IgnoringCase()
    {
        Assert.True(Winget().IsInstalledOn([App("Greenshot 1.3", "greenshot.greenshot")]));
    }

    [Fact]
    public void IsInstalledOn_MatchesTheExactName_IgnoringCase()
    {
        Assert.True(Custom().IsInstalledOn([App("zoho assist")]));
        Assert.False(Custom().IsInstalledOn([App("Zoho Assist Agent")]));
    }

    [Fact]
    public void IsInstalledOn_MatchesTheDetectAsPrefix_IgnoringCase()
    {
        var title = Custom(detectAs: "Zoho Assist");

        Assert.True(title.IsInstalledOn([App("ZOHO ASSIST Unattended Agent")]));
        Assert.False(title.IsInstalledOn([App("Zoho Mail")]));
    }

    [Fact]
    public void IsInstalledOn_IsFalse_ForAnEmptyInventory()
    {
        Assert.False(Winget().IsInstalledOn([]));
    }

    // ---- Applicability ----

    [Fact]
    public void AppliesTo_NeedsTheAssignment()
    {
        var title = Custom();
        Assert.False(title.AppliesTo(HostId, "Windows", []));

        title.Assign(false, [HostId], "admin@example.com");
        Assert.True(title.AppliesTo(HostId, "Windows", []));
        Assert.False(title.AppliesTo(Guid.NewGuid(), "Windows", []));
    }

    [Fact]
    public void AppliesTo_AllHosts_OnlyOnTheTitlesOs()
    {
        var title = Custom();
        title.Assign(true, [], "admin@example.com");

        Assert.True(title.AppliesTo(Guid.NewGuid(), "windows", []));
        Assert.False(title.AppliesTo(Guid.NewGuid(), "macOS", []));
    }

    [Fact]
    public void AppliesTo_APackageManagerTitle_OnlyWhereTheManagersOwnRowIsInInventory()
    {
        var title = Winget();
        title.Assign(true, [], "admin@example.com");

        // No winget on this host: not applicable, which the screen counts as N/A, never as a failure.
        Assert.False(title.AppliesTo(HostId, "Windows", [App("Firefox")]));
        Assert.False(title.HasPackageManager([App("Firefox")]));
        Assert.True(title.AppliesTo(HostId, "Windows", [App("WinGet")]));
    }

    [Fact]
    public void Assign_DeduplicatesHostIds_AndRecordsWhoAssigned()
    {
        var title = Custom();
        title.Assign(false, [HostId, HostId], "admin@example.com");

        Assert.Single(title.AssignedHostIds);
        Assert.Equal("admin@example.com", title.AssignedBy);
    }

    // ---- Invariants ----

    [Fact]
    public void Create_APackageManagerTitle_NeedsAPackageId()
    {
        Assert.Throws<DomainException>(() =>
            SoftwareTitle.Create("Greenshot", "pm:winget", "winget", "Windows", " ", null, SoftwareMode.Required, [], null));
    }

    [Fact]
    public void Create_ACustomTitle_RefusesAPackageId_BecauseItsAppIdIsItsName()
    {
        Assert.Throws<DomainException>(() =>
            SoftwareTitle.Create("Zoho Assist", "Windows", null, "Windows", "zoho.assist", null, SoftwareMode.Required, [], null));
    }

    [Theory]
    [InlineData("lower")]
    [InlineData("1LEADING_DIGIT")]
    [InlineData("HAS-DASH")]
    [InlineData("")]
    public void SecretNames_MustBeUpperSnakeCase(string name)
    {
        Assert.False(ScriptSecret.IsValidName(name));
        Assert.Throws<DomainException>(() => Custom(SoftwareMode.Required, null, name));
    }

    [Fact]
    public void SecretNames_RefuseATrailingNewline_WhichDotNetsDollarWouldHaveLetThrough()
    {
        // A title trims the names it is given, so this guards ScriptSecret.Create and the validator.
        Assert.False(ScriptSecret.IsValidName("TRAILING_NEWLINE\n"));
        Assert.Throws<DomainException>(() => ScriptSecret.Create("TRAILING_NEWLINE\n", "TEST_SECRET_VALUE", "sig", null));
    }

    [Fact]
    public void SecretNames_UpToSixtyFourCharacters()
    {
        Assert.True(ScriptSecret.IsValidName("A" + new string('B', 63)));
        Assert.False(ScriptSecret.IsValidName("A" + new string('B', 64)));
    }

    [Fact]
    public void ScriptSecret_RefusesAnEmptyValue_OrAMissingSignature()
    {
        Assert.Throws<DomainException>(() => ScriptSecret.Create("TEST_SECRET_A", "", "sig", null));
        Assert.Throws<DomainException>(() => ScriptSecret.Create("TEST_SECRET_A", "TEST_SECRET_VALUE", "", null));
    }

    [Fact]
    public void ScriptSecret_Set_ReplacesTheValueSignatureAndWho()
    {
        var secret = ScriptSecret.Create("TEST_SECRET_A", "TEST_SECRET_VALUE", "sig-1", "first@example.com");
        secret.Set("TEST_SECRET_VALUE_2", "sig-2", "second@example.com");

        Assert.Equal("TEST_SECRET_VALUE_2", secret.Value);
        Assert.Equal("sig-2", secret.ValueSignature);
        Assert.Equal("second@example.com", secret.UpdatedBy);
    }
}
