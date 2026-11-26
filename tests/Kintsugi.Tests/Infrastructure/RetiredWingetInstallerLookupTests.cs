using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Infrastructure.AppInstaller;

namespace Kintsugi.Tests.Infrastructure;

/// <summary>
/// HTWHPBCD5120MTY could not upgrade LibreOffice 7.6.7.2: its cached MSI was gone, and winget no longer lists 7.6.7.2,
/// so the old MSI could not be put back. winget-pkgs still holds the manifest it removed, and The Document Foundation's
/// archive still serves the file. Against the shapes GitHub answered for that version (trimmed), not a live call.
/// </summary>
public class RetiredWingetInstallerLookupTests : IDisposable
{
    private const string X64ProductCode = "{F77B9F35-B52D-4C13-AE7D-1F4C8127C505}";
    private const string RemovalCommit = "acafec14671d0e5dbd9b8b8f57a8bba5ee3c1b4e";
    private const string BeforeRemoval = "7e41d92d61421d6fb0af8528f146107b78373462";
    private const string HelpPackRemoval = "0e5c089836aa000000000000000000000000beef";
    private const string StableUrl = "https://download.documentfoundation.org/libreoffice/stable/7.6.7/win/x86_64/LibreOffice_7.6.7_Win_x86-64.msi";
    private const string ArchiveUrl = "https://downloadarchive.documentfoundation.org/libreoffice/old/7.6.7.2/win/x86_64/LibreOffice_7.6.7.2_Win_x86-64.msi";

    /// <summary>The removed manifest as winget-pkgs held it the commit before, with its file-extension list cut.</summary>
    private const string RemovedManifest = """
        # Created with YamlCreate.ps1 v2.4.1 Dumplings Mod $debug=QUSU.CRLF.7-4-2.Win32NT
        # yaml-language-server: $schema=https://aka.ms/winget-manifest.installer.1.6.0.schema.json

        PackageIdentifier: TheDocumentFoundation.LibreOffice.LTS
        PackageVersion: 7.6.7.2
        InstallerType: msi
        Scope: machine
        InstallerSwitches:
          InstallLocation: INSTALLLOCATION="<INSTALLPATH>"
        UpgradeBehavior: install
        Protocols:
        - ms-excel
        FileExtensions:
        - "123"
        - xpm
        ReleaseDate: 2024-05-10
        Installers:
        - Architecture: x86
          InstallerUrl: https://download.documentfoundation.org/libreoffice/stable/7.6.7/win/x86/LibreOffice_7.6.7_Win_x86.msi
          InstallerSha256: 3EBC33A80DCA6A1D190FD98714AC9B5626CA374E0EA8098CE9E049B92316A477
          ProductCode: '{0D89DC9D-066B-436E-B939-20775D164A67}'
        - Architecture: x64
          InstallerUrl: https://download.documentfoundation.org/libreoffice/stable/7.6.7/win/x86_64/LibreOffice_7.6.7_Win_x86-64.msi
          InstallerSha256: 89D0BE012FA078AA32E210665170215FDC5F2D17B2BBA1DFFCFA4EAC9D002F67
          ProductCode: '{F77B9F35-B52D-4C13-AE7D-1F4C8127C505}'
        - Architecture: arm64
          InstallerUrl: https://download.documentfoundation.org/libreoffice/stable/7.6.7/win/aarch64/LibreOffice_7.6.7_Win_aarch64.msi
          InstallerSha256: C9F5957671DD2CB140D4419D76BC9980E792D80E8B92655F9AC84C312C42EE61
          ProductCode: '{998A4F01-72BB-46D9-A3C9-3C1E52F239B8}'
        ManifestType: installer
        ManifestVersion: 1.6.0
        """;

    /// <summary>The help pack, published and removed beside it under its own id with its own ProductCode.</summary>
    private const string HelpPackManifest = """
        PackageIdentifier: TheDocumentFoundation.LibreOffice.HelpPack.LTS
        PackageVersion: 7.6.7.2
        InstallerType: msi
        Installers:
        - Architecture: x64
          InstallerUrl: https://download.documentfoundation.org/libreoffice/stable/7.6.7/win/x86_64/LibreOffice_7.6.7_Win_x86-64_helppack_en-US.msi
          InstallerSha256: 1111111111111111111111111111111111111111111111111111111111111111
          ProductCode: '{11111111-1111-1111-1111-111111111111}'
        ManifestType: installer
        ManifestVersion: 1.6.0
        """;

    private readonly List<string> _requests = new();
    private readonly HashSet<string> _answering = new(StringComparer.Ordinal) { ArchiveUrl };
    private HttpStatusCode _searchStatus = HttpStatusCode.OK;
    private string _removedManifest = RemovedManifest;

    public RetiredWingetInstallerLookupTests() => RetiredWingetInstallerLookup.Forget();

    public void Dispose() => RetiredWingetInstallerLookup.Forget();

    private static string Base64(string text) => Convert.ToBase64String(Encoding.UTF8.GetBytes(text));

    private RetiredWingetInstallerLookup Lookup()
    {
        var handler = new Handler(request =>
        {
            var url = request.RequestUri!.AbsoluteUri;
            _requests.Add(url);
            if (url.StartsWith("https://api.github.com/search/commits?", StringComparison.Ordinal))
            {
                return _searchStatus != HttpStatusCode.OK ? new HttpResponseMessage(_searchStatus) : Json($$"""
                    { "total_count": 4, "items": [
                      { "sha": "{{HelpPackRemoval}}", "commit": { "message": "Remove version: TheDocumentFoundation.LibreOffice.HelpPack.LTS version 7.6.7.2 (#162552)" } },
                      { "sha": "{{RemovalCommit}}", "commit": { "message": "Remove version: TheDocumentFoundation.LibreOffice.LTS version 7.6.7.2 (#162302)\n\nRemoved by the cleanup bot." } },
                      { "sha": "f71ffd00900000000000000000000000000000ff", "commit": { "message": "Fix: TheDocumentFoundation.LibreOffice.LTS version 7.6.7.2 (#156718)" } },
                      { "sha": "9999999999999999999999999999999999999999", "commit": { "message": "Remove version: Someone.Else version 7.6.7.2 (#1)" } } ] }
                    """);
            }

            if (url.EndsWith($"/commits/{RemovalCommit}", StringComparison.Ordinal))
            {
                return Json($$"""
                    { "sha": "{{RemovalCommit}}", "parents": [ { "sha": "{{BeforeRemoval}}" } ], "files": [
                      { "filename": "manifests/t/TheDocumentFoundation/LibreOffice/LTS/7.6.7.2/TheDocumentFoundation.LibreOffice.LTS.installer.yaml", "status": "removed" },
                      { "filename": "manifests/t/TheDocumentFoundation/LibreOffice/LTS/7.6.7.2/TheDocumentFoundation.LibreOffice.LTS.yaml", "status": "removed" } ] }
                    """);
            }

            if (url.EndsWith($"/commits/{HelpPackRemoval}", StringComparison.Ordinal))
            {
                return Json($$"""
                    { "sha": "{{HelpPackRemoval}}", "parents": [ { "sha": "abababababababababababababababababababab" } ], "files": [
                      { "filename": "manifests/t/TheDocumentFoundation/LibreOffice/HelpPack/LTS/7.6.7.2/TheDocumentFoundation.LibreOffice.HelpPack.LTS.installer.yaml", "status": "removed" } ] }
                    """);
            }

            if (url.Contains("/contents/manifests/", StringComparison.Ordinal))
            {
                var manifest = url.Contains("HelpPack", StringComparison.Ordinal) ? HelpPackManifest
                    : url.EndsWith($"?ref={BeforeRemoval}", StringComparison.Ordinal) ? _removedManifest
                    : null;
                return manifest is null ? new HttpResponseMessage(HttpStatusCode.NotFound) : Json($$"""{ "content": "{{Base64(manifest)}}\n" }""");
            }

            if (!url.StartsWith("https://api.github.com/", StringComparison.Ordinal))
            {
                Assert.Null(request.Headers.Authorization);
                return new HttpResponseMessage(_answering.Contains(url) ? HttpStatusCode.OK : HttpStatusCode.NotFound);
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        var settings = new Mock<IGitHubSettingsProvider>();
        settings.Setup(s => s.GetAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GitHubSettingsSnapshot("htw-secops/kintsugi", "htw-secops/kintsugi-approvals", "read-only-token", "approval-token"));
        return new RetiredWingetInstallerLookup(new HttpClient(handler), settings.Object, NullLogger<RetiredWingetInstallerLookup>.Instance);
    }

    private static HttpResponseMessage Json(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    [Fact]
    public void ParseInstallers_ReadsEachInstallersAddressHashAndProductCode_AndTheSharedType()
    {
        var installers = RetiredWingetInstallerLookup.ParseInstallers(RemovedManifest.Replace("\n", "\r\n", StringComparison.Ordinal));

        Assert.Equal(new[] { "x86", "x64", "arm64" }, installers.Select(i => i.Architecture));
        var x64 = installers[1];
        Assert.Equal(StableUrl, x64.Url);
        Assert.Equal("89D0BE012FA078AA32E210665170215FDC5F2D17B2BBA1DFFCFA4EAC9D002F67", x64.Sha256);
        Assert.Equal(new[] { X64ProductCode }, x64.ProductCodes);
        Assert.True(x64.IsMsi);
    }

    [Fact]
    public void ParseInstallers_TakesProductCodesFromAppsAndFeaturesEntries_AndTheManifestsOwn()
    {
        var installers = RetiredWingetInstallerLookup.ParseInstallers("""
            PackageIdentifier: Vendor.App
            ProductCode: '{AAAAAAAA-AAAA-AAAA-AAAA-AAAAAAAAAAAA}'
            Installers:
              - Architecture: x64
                InstallerType: wix
                InstallerUrl: https://vendor.example.com/app.msi
                InstallerSha256: 2222222222222222222222222222222222222222222222222222222222222222
                AppsAndFeaturesEntries:
                  - DisplayName: App
                    ProductCode: "{bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb}"
                    InstallerType: exe
            """);

        var installer = Assert.Single(installers);
        Assert.Equal("wix", installer.Type);
        Assert.True(installer.IsMsi);
        Assert.Equal(new[] { "{BBBBBBBB-BBBB-BBBB-BBBB-BBBBBBBBBBBB}", "{AAAAAAAA-AAAA-AAAA-AAAA-AAAAAAAAAAAA}" }, installer.ProductCodes);
    }

    /// <summary>
    /// The id winget matches today is TheDocumentFoundation.LibreOffice; 7.6.7.2 was published as …LTS beside a help
    /// pack under its own id. The installed ProductCode picks the right manifest, read as it stood before the removal,
    /// and the stable address that is now a 404 gives way to the archive's.
    /// </summary>
    [Fact]
    public async Task FindAsync_ReadsTheRemovedManifestThatNamesTheInstalledProduct_AndUsesTheArchiveAddress()
    {
        var found = await Lookup().FindAsync("TheDocumentFoundation.LibreOffice", "7.6.7.2", "f77b9f35-b52d-4c13-ae7d-1f4c8127c505", CancellationToken.None);

        Assert.NotNull(found);
        Assert.Equal("TheDocumentFoundation.LibreOffice.LTS", found.PackageIdentifier);
        Assert.Equal(ArchiveUrl, found.Url);
        Assert.Equal("89d0be012fa078aa32e210665170215fdc5f2d17b2bba1dffcfa4eac9d002f67", found.Sha256);
        Assert.Equal(X64ProductCode, found.ProductCode);
        Assert.Equal(BeforeRemoval, found.ManifestCommit);
        Assert.Contains(_requests, u => u.Contains(Uri.EscapeDataString("\"TheDocumentFoundation.LibreOffice\""), StringComparison.Ordinal));
        Assert.DoesNotContain(_requests, u => u.Contains("9999999999", StringComparison.Ordinal));
        Assert.Equal(new[] { StableUrl, ArchiveUrl }, _requests.Where(u => !u.StartsWith("https://api.github.com/", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task FindAsync_UsesTheManifestsOwnAddress_WhileItStillAnswers()
    {
        _answering.Add(StableUrl);

        var found = await Lookup().FindAsync("TheDocumentFoundation.LibreOffice", "7.6.7.2", X64ProductCode, CancellationToken.None);

        Assert.Equal(StableUrl, found?.Url);
        Assert.DoesNotContain(ArchiveUrl, _requests);
    }

    /// <summary>Hosts never download a binary from GitHub, so an installer published as a GitHub release is no answer,
    /// and the address is not even tried.</summary>
    [Fact]
    public async Task FindAsync_GivesNothing_ForAnInstallerOnGitHub()
    {
        const string onGitHub = "https://github.com/vendor/app/releases/download/7.6.7.2/LibreOffice_7.6.7_Win_x86-64.msi";
        _removedManifest = RemovedManifest.Replace(StableUrl, onGitHub, StringComparison.Ordinal);
        _answering.Add(onGitHub);

        Assert.Null(await Lookup().FindAsync("TheDocumentFoundation.LibreOffice", "7.6.7.2", X64ProductCode, CancellationToken.None));
        Assert.DoesNotContain(onGitHub, _requests);
    }

    [Fact]
    public async Task FindAsync_GivesNothing_WhenNoAddressAnswers()
    {
        _answering.Clear();

        Assert.Null(await Lookup().FindAsync("TheDocumentFoundation.LibreOffice", "7.6.7.2", X64ProductCode, CancellationToken.None));
    }

    /// <summary>A product no manifest of that version names is never answered with one that is merely close.</summary>
    [Fact]
    public async Task FindAsync_GivesNothing_ForAProductCodeNoManifestNames_AndRemembersThat()
    {
        var lookup = Lookup();

        Assert.Null(await lookup.FindAsync("TheDocumentFoundation.LibreOffice", "7.6.7.2", "{00000000-0000-0000-0000-000000000000}", CancellationToken.None));
        var asked = _requests.Count;
        Assert.Null(await lookup.FindAsync("TheDocumentFoundation.LibreOffice", "7.6.7.2", "{00000000-0000-0000-0000-000000000000}", CancellationToken.None));
        Assert.Equal(asked, _requests.Count);
    }

    [Fact]
    public async Task FindAsync_AsksGitHubOncePerVersion()
    {
        var lookup = Lookup();
        await lookup.FindAsync("TheDocumentFoundation.LibreOffice", "7.6.7.2", X64ProductCode, CancellationToken.None);
        var asked = _requests.Count;

        var again = await lookup.FindAsync("TheDocumentFoundation.LibreOffice", "7.6.7.2", X64ProductCode, CancellationToken.None);

        Assert.Equal(ArchiveUrl, again?.Url);
        Assert.Equal(asked, _requests.Count);
    }

    /// <summary>GitHub being unreachable is not "none": it throws, so the host hears 502 and the next cycle asks again.</summary>
    [Fact]
    public async Task FindAsync_Throws_WhenGitHubCannotBeAsked_AndRemembersNothing()
    {
        _searchStatus = HttpStatusCode.Forbidden;
        var lookup = Lookup();

        await Assert.ThrowsAsync<HttpRequestException>(() => lookup.FindAsync("TheDocumentFoundation.LibreOffice", "7.6.7.2", X64ProductCode, CancellationToken.None));
        _searchStatus = HttpStatusCode.OK;
        Assert.Equal(ArchiveUrl, (await lookup.FindAsync("TheDocumentFoundation.LibreOffice", "7.6.7.2", X64ProductCode, CancellationToken.None))?.Url);
    }

    /// <summary>The route is anonymous, so what it is asked never reaches GitHub's search unless it is a plain id,
    /// version and GUID.</summary>
    [Theory]
    [InlineData("TheDocumentFoundation.LibreOffice\" repo:other/repo", "7.6.7.2", X64ProductCode)]
    [InlineData("NoDot", "7.6.7.2", X64ProductCode)]
    [InlineData("TheDocumentFoundation.LibreOffice", "7.6.7.2 \"x\"", X64ProductCode)]
    [InlineData("TheDocumentFoundation.LibreOffice", "7.6.7.2", "not-a-guid")]
    public async Task FindAsync_AsksNothing_ForAMalformedQuestion(string id, string version, string productCode)
    {
        Assert.Null(await Lookup().FindAsync(id, version, productCode, CancellationToken.None));
        Assert.Empty(_requests);
    }

    [Theory]
    [InlineData("{f77b9f35-b52d-4c13-ae7d-1f4c8127c505}", X64ProductCode)]
    [InlineData("F77B9F35-B52D-4C13-AE7D-1F4C8127C505", X64ProductCode)]
    [InlineData("{F77B9F35-B52D-4C13-AE7D}", null)]
    [InlineData("", null)]
    public void NormalizeProductCode_IsWindowsInstallersOwnForm(string value, string? expected) =>
        Assert.Equal(expected, RetiredWingetInstallerLookup.NormalizeProductCode(value));

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
}
