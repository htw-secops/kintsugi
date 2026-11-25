using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;
using Moq;
using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Infrastructure.AppInstaller;

namespace Kintsugi.Tests.Infrastructure;

/// <summary>
/// Kintsugi's copy of App Installer offers a file only when its bytes match GitHub's own SHA-256, and a refresh that
/// fails leaves the copy already on offer untouched. Against a captured release shape, not a live call.
/// </summary>
public class AppInstallerMirrorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "kintsugi-appinstaller-" + Guid.NewGuid().ToString("N"));
    private readonly Dictionary<string, byte[]> _files = new();
    private int _downloads;

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static string Sha(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private const string BundleName = "Microsoft.DesktopAppInstaller_8wekyb3d8bbwe.msixbundle";
    private const string DependenciesName = "DesktopAppInstaller_Dependencies.zip";
    private const string LicenseName = "e53e159d00e04f729cc2180cffd1c02e_License1.xml";

    /// <summary>The shape GitHub's releases/latest answered for winget-cli v1.29.380, trimmed to the fields read,
    /// with the files this test serves in place of the real ones.</summary>
    private string Release(string tag, string? digestOverride = null)
    {
        string Asset(string name) =>
            $$"""
            { "name": "{{name}}", "size": {{_files[name].Length}}, "digest": "sha256:{{(name == BundleName && digestOverride is not null ? digestOverride : Sha(_files[name]))}}",
              "browser_download_url": "https://github.com/microsoft/winget-cli/releases/download/{{tag}}/{{name}}" }
            """;
        return $$"""
            { "tag_name": "{{tag}}", "assets": [
                { "name": "DesktopAppInstallerPolicies.zip", "size": 41003, "digest": "sha256:a0793f0832ba540a55848deeee271c3686f2149cb4ead7fe72be2417c9f62605",
                  "browser_download_url": "https://github.com/microsoft/winget-cli/releases/download/{{tag}}/DesktopAppInstallerPolicies.zip" },
                {{Asset(DependenciesName)}}, {{Asset(LicenseName)}}, {{Asset(BundleName)}} ] }
            """;
    }

    private AppInstallerMirror Mirror(string releaseJson)
    {
        var handler = new Handler(request =>
        {
            var url = request.RequestUri!.ToString();
            if (url.EndsWith("/releases/latest", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(releaseJson, Encoding.UTF8, "application/json") };
            }

            _downloads++;
            var name = url[(url.LastIndexOf('/') + 1)..];
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(_files[name]) };
        });
        var settings = new Mock<IGitHubSettingsProvider>();
        settings.Setup(s => s.GetAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GitHubSettingsSnapshot("htw-secops/kintsugi", "htw-secops/kintsugi-approvals", null, null));
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["AgentPackages:Directory"] = _root })
            .Build();
        return new AppInstallerMirror(new HttpClient(handler), settings.Object, configuration);
    }

    private void Serve(string generation)
    {
        _files[BundleName] = Encoding.UTF8.GetBytes("bundle " + generation);
        _files[DependenciesName] = Encoding.UTF8.GetBytes("dependencies " + generation);
        _files[LicenseName] = Encoding.UTF8.GetBytes("<License />");
    }

    [Fact]
    public async Task ARefresh_OffersTheThreeFiles_WithGitHubsHashes()
    {
        Serve("a");
        var mirror = Mirror(Release("v1.29.380"));

        var result = await mirror.RefreshAsync(CancellationToken.None);

        Assert.True(result.Changed);
        Assert.Equal("1.29.380", result.Offer.Version);
        Assert.Equal(new[] { "bundle", "dependencies", "license" }, result.Offer.Files.Select(f => f.Role));
        Assert.Equal(Sha(_files[BundleName]), result.Offer.Files.Single(f => f.Role == "bundle").Sha256);
        Assert.DoesNotContain(result.Offer.Files, f => f.Name == "DesktopAppInstallerPolicies.zip");

        var offer = await mirror.GetOfferAsync(CancellationToken.None);
        Assert.Equal("1.29.380", offer!.Version);
        await using var stream = await mirror.OpenFileAsync(BundleName, CancellationToken.None);
        using var reader = new StreamReader(stream!);
        Assert.Equal("bundle a", await reader.ReadToEndAsync());
    }

    [Fact]
    public async Task TheSameReleaseAgain_DownloadsNothing()
    {
        Serve("a");
        await Mirror(Release("v1.29.380")).RefreshAsync(CancellationToken.None);
        _downloads = 0;

        var result = await Mirror(Release("v1.29.380")).RefreshAsync(CancellationToken.None);

        Assert.False(result.Changed);
        Assert.Equal(0, _downloads);
    }

    [Fact]
    public async Task AFileThatDoesNotMatchGitHubsHash_IsNeverOffered_AndTheOldCopyStays()
    {
        Serve("a");
        await Mirror(Release("v1.29.380")).RefreshAsync(CancellationToken.None);
        Serve("b");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Mirror(Release("v1.30.100", digestOverride: new string('0', 64))).RefreshAsync(CancellationToken.None));

        Assert.Contains("does not match the SHA-256 GitHub lists", error.Message);
        var mirror = Mirror(Release("v1.29.380"));
        Assert.Equal("1.29.380", (await mirror.GetOfferAsync(CancellationToken.None))!.Version);
        Assert.Equal(new[] { "1.29.380" }, Directory.GetDirectories(Path.Combine(_root, "app-installer")).Select(Path.GetFileName));
    }

    [Fact]
    public async Task ANewerRelease_ReplacesTheOldCopy()
    {
        Serve("a");
        await Mirror(Release("v1.29.380")).RefreshAsync(CancellationToken.None);
        Serve("b");

        var result = await Mirror(Release("v1.30.100")).RefreshAsync(CancellationToken.None);

        Assert.True(result.Changed);
        Assert.Equal(new[] { "1.30.100" }, Directory.GetDirectories(Path.Combine(_root, "app-installer")).Select(Path.GetFileName));
    }

    [Theory]
    [InlineData("../offer.json")]
    [InlineData("DesktopAppInstallerPolicies.zip")]
    [InlineData("offer.json")]
    public async Task OnlyAFileTheOfferLists_CanBeOpened(string name)
    {
        Serve("a");
        var mirror = Mirror(Release("v1.29.380"));
        await mirror.RefreshAsync(CancellationToken.None);

        Assert.Null(await mirror.OpenFileAsync(name, CancellationToken.None));
    }

    [Fact]
    public void AReleaseWithoutGitHubsDigest_IsRefused()
    {
        Serve("a");
        var json = Release("v1.29.380").Replace($"\"digest\": \"sha256:{Sha(_files[BundleName])}\",", "", StringComparison.Ordinal);

        var error = Assert.Throws<InvalidOperationException>(() => AppInstallerMirror.ParseRelease(json));

        Assert.Contains("has no SHA-256 from GitHub", error.Message);
    }

    [Fact]
    public void AFileOfferedFromAnotherAddress_IsRefused()
    {
        Serve("a");
        var json = Release("v1.29.380").Replace(
            $"https://github.com/microsoft/winget-cli/releases/download/v1.29.380/{BundleName}", "https://example.com/bundle.msixbundle", StringComparison.Ordinal);

        Assert.Throws<InvalidOperationException>(() => AppInstallerMirror.ParseRelease(json));
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
}
