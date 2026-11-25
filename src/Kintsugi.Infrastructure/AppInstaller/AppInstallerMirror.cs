using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Infrastructure.ScriptApproval;
using Kintsugi.Infrastructure.Storage;

namespace Kintsugi.Infrastructure.AppInstaller;

/// <summary>
/// Keeps one copy of the latest winget-cli release's App Installer bundle, its dependencies archive and its
/// licence under <c>&lt;AgentPackages:Directory&gt;/app-installer/&lt;version&gt;/</c>, with an <c>offer.json</c>
/// beside it that says what is there. See <see cref="IAppInstallerMirror"/> for why it exists.
/// </summary>
/// <remarks>
/// <para>
/// <strong>GitHub's own SHA-256 is the check here.</strong> The release API gives each asset a
/// <c>digest</c>; a file whose bytes do not match it is never offered, and a release without one is refused
/// rather than trusted. The host checks the file against the hash offered and each package's Microsoft
/// signature again before installing anything, so this server can only ever hand out a genuine Microsoft build.
/// </para>
/// <para>
/// <strong>A refresh that fails leaves the published offer alone.</strong> Files are written to
/// <c>&lt;version&gt;.partial</c>, moved into place only when every one checks out, and <c>offer.json</c> is
/// replaced last, in one rename. Older versions are deleted afterwards, so the volume holds one release (about
/// 320 MB) and, during a refresh, two.
/// </para>
/// </remarks>
public class AppInstallerMirror : IAppInstallerMirror
{
    private const string LatestReleaseUrl = "https://api.github.com/repos/microsoft/winget-cli/releases/latest";
    private const string DownloadPrefix = "https://github.com/microsoft/winget-cli/releases/download/";
    private const string OfferFileName = "offer.json";

    /// <summary>The three files the winget script installs, by what each is for. Matched on the name the release
    /// gives it, so a renamed asset stops the refresh rather than offering the wrong file.</summary>
    private static readonly (string Role, Regex Name)[] Roles =
    [
        ("bundle", new Regex(@"^Microsoft\.DesktopAppInstaller_8wekyb3d8bbwe\.msixbundle$", RegexOptions.CultureInvariant)),
        ("dependencies", new Regex(@"^DesktopAppInstaller_Dependencies\.zip$", RegexOptions.CultureInvariant)),
        ("license", new Regex(@"^[0-9A-Fa-f]+_License1\.xml$", RegexOptions.CultureInvariant)),
    ];

    private static readonly Regex VersionPattern = new(@"^\d+(\.\d+){1,3}$", RegexOptions.CultureInvariant);
    private static readonly Regex DigestPattern = new("^sha256:([0-9a-f]{64})$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // One refresh at a time per process: the background service and nothing else calls it, but two would write
    // the same staging folder.
    private static readonly SemaphoreSlim RefreshLock = new(1, 1);

    private readonly HttpClient _httpClient;
    private readonly IGitHubSettingsProvider _settingsProvider;
    private readonly string _root;

    /// <summary>How long one refresh's downloads may take together. <see cref="HttpClient.Timeout"/> stops at the
    /// response headers when a body is streamed, so without this a download that stalls part-way holds
    /// <see cref="RefreshLock"/>, and every later refresh behind it, until the API restarts.</summary>
    public TimeSpan DownloadDeadline { get; init; } = TimeSpan.FromMinutes(30);

    public AppInstallerMirror(HttpClient httpClient, IGitHubSettingsProvider settingsProvider, IConfiguration configuration)
    {
        _httpClient = httpClient;
        _settingsProvider = settingsProvider;
        _root = Path.Combine(configuration["AgentPackages:Directory"] ?? "/data/agent-packages", "app-installer");
        ScriptApprovalGitHubHeaders.ApplyStaticHeaders(_httpClient);
    }

    public async Task<AppInstallerOffer?> GetOfferAsync(CancellationToken cancellationToken)
    {
        var path = Path.Combine(_root, OfferFileName);
        if (!File.Exists(path))
        {
            return null;
        }

        // A damaged offer is no offer: hosts get the 404 the winget script handles rather than a 500, and the next
        // refresh writes a new one.
        try
        {
            await using var stream = File.OpenRead(path);
            var offer = await JsonSerializer.DeserializeAsync<AppInstallerOffer>(stream, Json, cancellationToken);
            return offer is { Version: not null, Files: not null } ? offer : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public async Task<Stream?> OpenFileAsync(string fileName, CancellationToken cancellationToken)
    {
        var offer = await GetOfferAsync(cancellationToken);
        if (offer is null || !offer.Files.Any(f => string.Equals(f.Name, fileName, StringComparison.Ordinal)))
        {
            return null;
        }

        try
        {
            return File.OpenRead(Path.Combine(
                _root,
                AgentPackageFileStorage.RequireBareName(offer.Version, nameof(offer.Version)),
                AgentPackageFileStorage.RequireBareName(fileName, nameof(fileName))));
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            // offer.json names a folder that is gone; RefreshAsync downloads it again.
            return null;
        }
    }

    public async Task<AppInstallerRefreshResult> RefreshAsync(CancellationToken cancellationToken)
    {
        await RefreshLock.WaitAsync(cancellationToken);
        try
        {
            var (version, assets) = ParseRelease(await ReadLatestReleaseAsync(cancellationToken));
            var current = await GetOfferAsync(cancellationToken);
            if (current is not null && current.Version == version && Directory.Exists(Path.Combine(_root, version)))
            {
                return new AppInstallerRefreshResult(current, Changed: false);
            }

            Directory.CreateDirectory(_root);
            var staging = Path.Combine(_root, version + ".partial");
            if (Directory.Exists(staging))
            {
                Directory.Delete(staging, recursive: true);
            }

            Directory.CreateDirectory(staging);
            var files = new List<AppInstallerFile>();
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(DownloadDeadline);
            try
            {
                foreach (var asset in assets)
                {
                    files.Add(await DownloadAsync(asset, staging, deadline.Token));
                }
            }
            catch (Exception ex)
            {
                // Not left behind: a failed bundle download is a couple of hundred megabytes on a shared volume.
                Directory.Delete(staging, recursive: true);
                if (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested)
                {
                    throw new TimeoutException($"Downloading winget-cli {version} took longer than {DownloadDeadline}.", ex);
                }

                throw;
            }

            var final = Path.Combine(_root, version);
            if (Directory.Exists(final))
            {
                Directory.Delete(final, recursive: true);
            }

            Directory.Move(staging, final);

            var offer = new AppInstallerOffer(version, files, DateTimeOffset.UtcNow);
            var temporary = Path.Combine(_root, OfferFileName + ".tmp");
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(offer, Json), cancellationToken);
            File.Move(temporary, Path.Combine(_root, OfferFileName), overwrite: true);

            foreach (var directory in Directory.GetDirectories(_root))
            {
                if (!string.Equals(Path.GetFileName(directory), version, StringComparison.Ordinal))
                {
                    Directory.Delete(directory, recursive: true);
                }
            }

            return new AppInstallerRefreshResult(offer, Changed: true);
        }
        finally
        {
            RefreshLock.Release();
        }
    }

    public sealed record ReleaseAsset(string Role, string Name, string Url, long Size, string Sha256);

    /// <summary>The release tag as a version and the three assets, each with GitHub's SHA-256. Pure, so the shape
    /// of GitHub's answer is tested without a network.</summary>
    public static (string Version, IReadOnlyList<ReleaseAsset> Assets) ParseRelease(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var tag = root.TryGetProperty("tag_name", out var tagElement) ? tagElement.GetString() : null;
        var version = tag?.TrimStart('v', 'V') ?? "";
        if (!VersionPattern.IsMatch(version))
        {
            throw new InvalidOperationException($"The latest winget-cli release has an unexpected tag '{tag}'.");
        }

        var assets = new List<ReleaseAsset>();
        foreach (var (role, pattern) in Roles)
        {
            var match = root.GetProperty("assets").EnumerateArray()
                .FirstOrDefault(a => a.TryGetProperty("name", out var n) && pattern.IsMatch(n.GetString() ?? ""));
            if (match.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidOperationException($"winget-cli {version} carries no {role} file.");
            }

            var name = match.GetProperty("name").GetString()!;
            var url = match.TryGetProperty("browser_download_url", out var u) ? u.GetString() ?? "" : "";
            if (!url.StartsWith(DownloadPrefix, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"{name} is offered from an unexpected address: {url}");
            }

            var digest = match.TryGetProperty("digest", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() ?? "" : "";
            var sha = DigestPattern.Match(digest);
            if (!sha.Success)
            {
                throw new InvalidOperationException($"{name} in winget-cli {version} has no SHA-256 from GitHub to check it against.");
            }

            var size = match.TryGetProperty("size", out var s) && s.TryGetInt64(out var bytes) ? bytes : 0;
            assets.Add(new ReleaseAsset(role, name, url, size, sha.Groups[1].Value.ToLowerInvariant()));
        }

        return (version, assets);
    }

    private async Task<string> ReadLatestReleaseAsync(CancellationToken cancellationToken)
    {
        // The read-only token, only to lift the anonymous rate limit; never the approval token.
        var settings = await _settingsProvider.GetAsync(cancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        using var request = ScriptApprovalGitHubHeaders.Request(HttpMethod.Get, LatestReleaseUrl, settings.ApiToken);
        using var response = await _httpClient.SendAsync(request, timeout.Token);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(timeout.Token);
    }

    private async Task<AppInstallerFile> DownloadAsync(ReleaseAsset asset, string folder, CancellationToken cancellationToken)
    {
        // No token: the files are public, and the download redirects to another host.
        using var request = new HttpRequestMessage(HttpMethod.Get, asset.Url);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));
        // Streamed to disk: the bundle alone is over 200 MB, more than this pod should hold in memory.
        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        var path = Path.Combine(folder, AgentPackageFileStorage.RequireBareName(asset.Name, nameof(asset.Name)));
        using var sha256 = SHA256.Create();
        await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken))
        await using (var destination = File.Create(path))
        await using (var hashing = new CryptoStream(destination, sha256, CryptoStreamMode.Write))
        {
            await source.CopyToAsync(hashing, cancellationToken);
        }

        var actual = Convert.ToHexString(sha256.Hash!).ToLowerInvariant();
        if (actual != asset.Sha256)
        {
            throw new InvalidOperationException($"{asset.Name} does not match the SHA-256 GitHub lists for it ({actual}, expected {asset.Sha256}).");
        }

        return new AppInstallerFile(asset.Role, asset.Name, actual, new FileInfo(path).Length);
    }
}
