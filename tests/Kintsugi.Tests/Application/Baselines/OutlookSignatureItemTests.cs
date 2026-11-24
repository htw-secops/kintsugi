using System.Text.Json;
using System.Text.RegularExpressions;
using Kintsugi.Application.Baselines;
using Kintsugi.Application.ScriptApproval;
using Kintsugi.WebApi.Controllers;
using static Kintsugi.Tests.Application.Baselines.BaselineTestData;

namespace Kintsugi.Tests.Application.Baselines;

/// <summary>Contract items 1 and 2 of IP-874: the <c>outlook-signature</c> item's rules, and the fixed list of
/// what it can ever write.</summary>
public class OutlookSignatureItemTests
{
    private static bool Valid(BaselineItem item) => new BaselineItemValidator().Validate(item).IsValid;

    [Theory]
    [InlineData("enforce")]
    [InlineData("audit")]
    public void AValidItem_IsAccepted_InEitherMode(string mode)
    {
        Assert.True(Valid(OutlookSignature() with { Mode = mode }));
        Assert.True(new BaselineItemsValidator().Validate(new[] { OutlookSignature(), Setting("copilot.off") }).IsValid);
    }

    public static IEnumerable<object[]> Broken() => new[]
    {
        new object[] { "machine scope", OutlookSignature() with { Scope = "machine", Hive = "HKLM" } },
        new object[] { "HKLM", OutlookSignature() with { Hive = "HKLM" } },
        new object[] { "another path", OutlookSignature() with { Path = @"Software\Microsoft\Office\16.0\Outlook\Profiles" } },
        new object[] { "path in other case", OutlookSignature() with { Path = @"software\microsoft\office\16.0\outlook" } },
        new object[] { "a value name", OutlookSignature() with { ValueName = "NewSignature" } },
        new object[] { "a value type", OutlookSignature() with { ValueType = "string" } },
        new object[] { "data", OutlookSignature() with { Data = "Example (managed)" } },
        new object[] { "a secret", OutlookSignature() with { Secret = "SIGNATURE" } },
        new object[] { "no mode", OutlookSignature() with { Mode = null } },
        new object[] { "another mode", OutlookSignature() with { Mode = "set" } },
        new object[] { "no name", OutlookSignature() with { SignatureName = null } },
    };

    [Theory]
    [MemberData(nameof(Broken))]
    public void AnythingElse_IsRefused(string why, BaselineItem item) => Assert.False(Valid(item), why);

    [Theory]
    [InlineData("Example (managed)")]
    [InlineData("A")]
    [InlineData("Example_Signature-2.v1")]
    [InlineData("0123456789012345678901234567890123456789012345678901234567890123")]
    public void ASignatureName_IsAWindowsFileName(string name) =>
        Assert.True(Valid(OutlookSignature(name)));

    [Theory]
    [InlineData("")]
    [InlineData(" Example")]
    [InlineData("(managed)")]
    [InlineData("Example.")]
    [InlineData("Example ")]
    [InlineData(@"..\..\Startup\run")]
    [InlineData("Example/managed")]
    [InlineData("Example:managed")]
    [InlineData("Exämple")]
    [InlineData("01234567890123456789012345678901234567890123456789012345678901234")]
    public void ASignatureName_ThatIsNotABareFileName_IsRefused(string name) =>
        Assert.False(Valid(OutlookSignature(name)));

    [Fact]
    public void OnlyAnOutlookSignatureItem_NamesASignature()
    {
        Assert.False(Valid(Setting("copilot.off") with { SignatureName = "Example (managed)" }));
        Assert.False(Valid(Fact("edition") with { SignatureName = "Example (managed)" }));
    }

    [Fact]
    public void ABaseline_HoldsAtMostOneOutlookSignatureItem()
    {
        var result = new BaselineItemsValidator().Validate(new[] { OutlookSignature(), OutlookSignature() with { Key = "outlook.signature.2" } });

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.ErrorMessage.Contains("at most one"));
    }

    /// <summary>Enforce for this kind is bounded by the signature write list, not EnforceableValues, so it is
    /// accepted although its path is on neither.</summary>
    [Fact]
    public void Enforce_DoesNotNeedEnforceableValues()
    {
        Assert.False(BaselineItemRules.IsEnforceable(OutlookSignature()));
        Assert.True(Valid(OutlookSignature() with { Mode = "enforce" }));
    }

    /// <summary>The new optional field must not move a byte of any existing document. The hash was first pinned at
    /// what origin/main produced for this fixture under these ids before <c>signatureName</c> existed
    /// (<c>92b8ef82…</c>). IP-926 changed the fixture's lock items, not the serialiser, and re-pinned it at what
    /// the same code produces for the new items, so a change in how a document serialises still fails here.</summary>
    [Fact]
    public void TheMasterDailyFixturesHash_IsUnchanged()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Kintsugi.sln")))
        {
            directory = directory.Parent;
        }

        var fixture = JsonSerializer.Deserialize<CreateBaselineRequest>(
            File.ReadAllText(Path.Combine(directory!.FullName, "baselines", "master-daily.json")),
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        var document = BaselineDocument.ToCanonicalJson(new BaselineDocument(
            1, Guid.Parse("11111111-1111-1111-1111-111111111111"), fixture.Name,
            Guid.Parse("22222222-2222-2222-2222-222222222222"), 1, fixture.Items));

        Assert.DoesNotContain("signatureName", document);
        Assert.Equal("8113cb7e1c706d99e2d06bf942e17653bcdf17eafd4349ff27f44abe552d245f", ScriptContentHash.Of(document));
    }

    [Fact]
    public void AnOutlookSignatureItem_RoundTripsItsName_ThroughTheCanonicalDocument()
    {
        var json = BaselineDocument.ToCanonicalJson(new BaselineDocument(1, Guid.NewGuid(), "Signatures", Guid.NewGuid(), 1, new[] { OutlookSignature() }));

        Assert.Contains("\"kind\":\"outlook-signature\"", json);
        Assert.Contains("\"signatureName\":\"Example (managed)\"", json);
        Assert.Equal("Example (managed)", Assert.Single(BaselineDocument.Parse(json).Items).SignatureName);
    }

    /// <summary>Contract item 2, exactly: two files and four values, each holding only the signature name.</summary>
    [Fact]
    public void TheSignatureWriteList_IsExactlyTheContract()
    {
        const string Account = @"Software\Microsoft\Office\16.0\Outlook\Profiles\<profile>\9375CFF0413111d3B88A00104B2A6676\<account>";
        const string Mail = @"Software\Microsoft\Office\16.0\Common\MailSettings";

        Assert.Equal(
            new[]
            {
                new SignatureWrite("file", @"<RoamingAppData>\Microsoft\Signatures\<signatureName>.htm", "", ""),
                new SignatureWrite("file", @"<RoamingAppData>\Microsoft\Signatures\<signatureName>.txt", "", ""),
                new SignatureWrite("HKCU", Account, "New Signature", "string"),
                new SignatureWrite("HKCU", Account, "Reply-Forward Signature", "string"),
                new SignatureWrite("HKCU", Mail, "NewSignature", "expandString"),
                new SignatureWrite("HKCU", Mail, "ReplySignature", "expandString"),
            },
            BaselineItemRules.SignatureWrites);
    }

    /// <summary>
    /// The agent's compiled-in list is the one that bounds a write, so it must be exactly this one. It is
    /// <c>baseline::SIGNATURE_WRITES</c> in <c>clients/windows-agent/src/baseline.rs</c>, in this shape, one tuple
    /// per <see cref="SignatureWrite"/> in the same field order (target, path, value name, value type):
    /// <code>
    /// const SIGNATURE_WRITES: [(&amp;str, &amp;str, &amp;str, &amp;str); 6] = [
    ///     ("file", r"&lt;RoamingAppData&gt;\Microsoft\Signatures\&lt;signatureName&gt;.htm", "", ""),
    ///     ...
    /// ];
    /// </code>
    /// </summary>
    [Fact]
    public void TheAgentsSignatureWriteList_IsExactlyTheServers()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Kintsugi.sln")))
        {
            directory = directory.Parent;
        }

        var source = File.ReadAllText(Path.Combine(directory!.FullName, "clients", "windows-agent", "src", "baseline.rs"));
        var block = Regex.Match(source, @"const SIGNATURE_WRITES: \[\(&str, &str, &str, &str\); \d+\] = \[(?<entries>.*?)\];", RegexOptions.Singleline);
        Assert.True(block.Success, "baseline.rs does not declare SIGNATURE_WRITES in the shape this test reads");

        var agent = Regex.Matches(block.Groups["entries"].Value,
                @"\(""(?<target>[^""]*)"", r?""(?<path>[^""]+)"", ""(?<name>[^""]*)"", ""(?<type>[^""]*)""\)")
            .Select(m => new SignatureWrite(m.Groups["target"].Value, m.Groups["path"].Value, m.Groups["name"].Value, m.Groups["type"].Value))
            .ToList();

        Assert.Equal(BaselineItemRules.SignatureWrites, agent);
    }
}
