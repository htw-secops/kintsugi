using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Infrastructure.ScriptApproval;

namespace Kintsugi.Infrastructure.AppInstaller;

/// <summary>
/// Reads the installer of a version winget no longer lists from the manifest microsoft/winget-pkgs held for it, found
/// through the commit that added, changed or removed it. See <see cref="IRetiredWingetInstallerLookup"/> for why.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The manifest's SHA-256 is the trust anchor, not the address.</strong> winget-pkgs reviewed that hash when
/// the version was published; the host downloads from whatever address is given here and refuses bytes that do not
/// match it, and then re-caches the MSI only when its signature is valid and its ProductCode is the installed
/// product's. So an address rule that points somewhere wrong costs a failed download, never a wrong install, and
/// nothing here downloads the file itself.
/// </para>
/// <para>
/// <strong>The ProductCode picks the manifest.</strong> A version is often published under several ids at once
/// (LibreOffice 7.6.7.2 as <c>…LibreOffice.LTS</c>, <c>…HelpPack.LTS</c> and <c>…SDK.LTS</c>), and the id the host
/// knows is the one winget matched today, which may not be any of them. Only the installer whose manifest names the
/// installed ProductCode is returned.
/// </para>
/// </remarks>
public class RetiredWingetInstallerLookup : IRetiredWingetInstallerLookup
{
    private const string RepositoryApi = "https://api.github.com/repos/microsoft/winget-pkgs";
    private const string CommitSearch = "https://api.github.com/search/commits";
    private const int MaxCandidates = 6;
    private static readonly TimeSpan FoundFor = TimeSpan.FromDays(1);
    private static readonly TimeSpan NotFoundFor = TimeSpan.FromHours(1);

    // Ids are Publisher.Package[.More]: dotted segments of letters, digits and - _ +.
    private static readonly Regex PackageIdPattern = new(@"^[A-Za-z0-9][A-Za-z0-9\-_+]*(\.[A-Za-z0-9][A-Za-z0-9\-_+]*){1,7}$", RegexOptions.CultureInvariant);
    private static readonly Regex VersionPattern = new(@"^[0-9A-Za-z][0-9A-Za-z.\-_+]{0,63}$", RegexOptions.CultureInvariant);
    private static readonly Regex ProductCodePattern = new(@"^\{[0-9A-F]{8}-[0-9A-F]{4}-[0-9A-F]{4}-[0-9A-F]{4}-[0-9A-F]{12}\}$", RegexOptions.CultureInvariant);
    private static readonly Regex Sha256Pattern = new("^[0-9A-Fa-f]{64}$", RegexOptions.CultureInvariant);

    // "Remove version: TheDocumentFoundation.LibreOffice.LTS version 7.6.7.2 (#162302)", and the New, Modify and Fix
    // subjects winget-pkgs writes the same way.
    private static readonly Regex CommitSubjectPattern = new(@"^[^:\r\n]+:\s*(?<id>\S+)\s+version\s+(?<version>\S+)", RegexOptions.CultureInvariant);

    /// <summary>Where a vendor moves a release once a newer one ships, for a manifest address that no longer answers.
    /// Safe to be wrong: see the remarks.</summary>
    private static readonly (Regex Url, Func<Match, string, string> Archive)[] ArchiveRules =
    [
        // The Document Foundation serves a release from stable/<x.y.z>/ (what its manifests carry) only until the
        // next one, then from its archive under the full four-part version. LibreOffice 7.6.7.2's stable address
        // was a 404 by 2026, and the archive's copy has the SHA-256 the manifest recorded.
        (new Regex(@"^https://download\.documentfoundation\.org/libreoffice/stable/[^/]+/win/(?<arch>[^/]+)/LibreOffice_[^/_]+_Win_(?<rest>[^/]+\.msi)$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase),
            (match, version) => $"https://downloadarchive.documentfoundation.org/libreoffice/old/{version}/win/{match.Groups["arch"].Value}/LibreOffice_{version}_Win_{match.Groups["rest"].Value}"),
    ];

    // Answers, including "none", for every caller: a fleet that meets one retired version asks the same question
    // from every host, and GitHub's commit search allows 30 a minute.
    private static readonly ConcurrentDictionary<string, (RetiredWingetInstaller? Found, DateTimeOffset Until)> Answers = new();
    private static readonly SemaphoreSlim Gate = new(1, 1);

    private readonly HttpClient _httpClient;
    private readonly IGitHubSettingsProvider _settingsProvider;
    private readonly ILogger<RetiredWingetInstallerLookup> _logger;

    public RetiredWingetInstallerLookup(HttpClient httpClient, IGitHubSettingsProvider settingsProvider, ILogger<RetiredWingetInstallerLookup> logger)
    {
        _httpClient = httpClient;
        _settingsProvider = settingsProvider;
        _logger = logger;
        ScriptApprovalGitHubHeaders.ApplyStaticHeaders(_httpClient);
    }

    public async Task<RetiredWingetInstaller?> FindAsync(string packageId, string version, string productCode, CancellationToken cancellationToken)
    {
        var code = NormalizeProductCode(productCode);
        if (code is null || !PackageIdPattern.IsMatch(packageId) || !VersionPattern.IsMatch(version))
        {
            return null;
        }

        var key = $"{packageId}|{version}|{code}".ToUpperInvariant();
        if (Answers.TryGetValue(key, out var known) && known.Until > DateTimeOffset.UtcNow)
        {
            return known.Found;
        }

        // One lookup at a time, so concurrent hosts asking the same thing wait for the first answer instead of each
        // spending a search on it.
        await Gate.WaitAsync(cancellationToken);
        try
        {
            if (Answers.TryGetValue(key, out known) && known.Until > DateTimeOffset.UtcNow)
            {
                return known.Found;
            }

            var found = await ResolveAsync(packageId, version, code, cancellationToken);
            // The route is anonymous; a caller inventing keys must not grow this without bound.
            if (Answers.Count > 1000)
            {
                Answers.Clear();
            }

            Answers[key] = (found, DateTimeOffset.UtcNow + (found is null ? NotFoundFor : FoundFor));
            return found;
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>Drops every remembered answer. For tests, which share this process-wide memory.</summary>
    public static void Forget() => Answers.Clear();

    private async Task<RetiredWingetInstaller?> ResolveAsync(string packageId, string version, string productCode, CancellationToken cancellationToken)
    {
        // The read-only token, only to lift the anonymous rate limit; never the approval token.
        var token = (await _settingsProvider.GetAsync(cancellationToken)).ApiToken;
        var query = $"repo:microsoft/winget-pkgs \"{packageId}\" \"version {version}\"";
        using var search = await GetJsonAsync($"{CommitSearch}?q={Uri.EscapeDataString(query)}&sort=committer-date&order=desc&per_page=30", token, cancellationToken);

        // Newest first, one commit per id: for a removed version that is the removal, whose parent still holds the file.
        var candidates = new List<(string Id, string Sha)>();
        foreach (var item in search.RootElement.GetProperty("items").EnumerateArray())
        {
            var match = CommitSubjectPattern.Match(item.GetProperty("commit").GetProperty("message").GetString() ?? "");
            var id = match.Groups["id"].Value;
            if (!match.Success
                || !string.Equals(match.Groups["version"].Value, version, StringComparison.OrdinalIgnoreCase)
                || !IsSameFamily(id, packageId)
                || candidates.Any(c => string.Equals(c.Id, id, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            candidates.Add((id, item.GetProperty("sha").GetString() ?? ""));
            if (candidates.Count == MaxCandidates)
            {
                break;
            }
        }

        foreach (var (id, sha) in candidates)
        {
            if (await ReadManifestAsync(id, version, sha, token, cancellationToken) is not { } manifest)
            {
                continue;
            }

            var installer = manifest.Installers.FirstOrDefault(i =>
                i.IsMsi && i.Url is not null && i.Sha256 is not null && i.ProductCodes.Contains(productCode, StringComparer.OrdinalIgnoreCase));
            if (installer is null)
            {
                continue;
            }

            foreach (var address in Addresses(installer.Url!, version))
            {
                if (await AnswersAsync(address, cancellationToken))
                {
                    return new RetiredWingetInstaller(id, version, productCode, address, installer.Sha256!.ToLowerInvariant(), manifest.Commit);
                }
            }

            _logger.LogWarning(
                "winget-pkgs holds {PackageId} {Version} ({ProductCode}) at {Commit}, but no address for its installer answers: {Url}",
                id, version, productCode, manifest.Commit, installer.Url);
            return null;
        }

        return null;
    }

    /// <summary>The same package, or one filed under it or above it: <c>TheDocumentFoundation.LibreOffice.LTS</c> for
    /// <c>TheDocumentFoundation.LibreOffice</c>. The ProductCode, not this, decides which one is used.</summary>
    private static bool IsSameFamily(string id, string packageId) =>
        string.Equals(id, packageId, StringComparison.OrdinalIgnoreCase)
        || id.StartsWith(packageId + ".", StringComparison.OrdinalIgnoreCase)
        || packageId.StartsWith(id + ".", StringComparison.OrdinalIgnoreCase);

    private async Task<(IReadOnlyList<ManifestInstaller> Installers, string Commit)?> ReadManifestAsync(
        string id, string version, string sha, string? token, CancellationToken cancellationToken)
    {
        using var commit = await GetJsonAsync($"{RepositoryApi}/commits/{Uri.EscapeDataString(sha)}", token, cancellationToken);
        var files = commit.RootElement.TryGetProperty("files", out var list)
            ? list.EnumerateArray().Select(f => (Name: f.GetProperty("filename").GetString() ?? "", Status: f.GetProperty("status").GetString() ?? "")).ToList()
            : [];

        // A multi-file manifest keeps its installers in <id>.installer.yaml; an old singleton manifest is <id>.yaml.
        var file = files.FirstOrDefault(f => f.Name.EndsWith($"/{version}/{id}.installer.yaml", StringComparison.OrdinalIgnoreCase));
        if (file.Name is null)
        {
            file = files.FirstOrDefault(f => f.Name.EndsWith($"/{version}/{id}.yaml", StringComparison.OrdinalIgnoreCase));
        }

        if (file.Name is null || !file.Name.StartsWith("manifests/", StringComparison.Ordinal))
        {
            return null;
        }

        // A removal deletes the file, so it is read as it stood just before.
        var at = file.Status == "removed"
            ? commit.RootElement.GetProperty("parents")[0].GetProperty("sha").GetString() ?? ""
            : sha;
        using var content = await GetJsonAsync(
            $"{RepositoryApi}/contents/{string.Join('/', file.Name.Split('/').Select(Uri.EscapeDataString))}?ref={Uri.EscapeDataString(at)}",
            token, cancellationToken);
        var yaml = Encoding.UTF8.GetString(Convert.FromBase64String((content.RootElement.GetProperty("content").GetString() ?? "").Replace("\n", "")));
        return (ParseInstallers(yaml), at);
    }

    private static IEnumerable<string> Addresses(string url, string version)
    {
        yield return url;
        foreach (var (pattern, archive) in ArchiveRules)
        {
            if (pattern.Match(url) is { Success: true } match)
            {
                yield return archive(match, version);
            }
        }
    }

    /// <summary>Whether a download address answers, read no further than the response headers. A GET rather than a
    /// HEAD, because not every vendor answers HEAD.</summary>
    private async Task<bool> AnswersAsync(string url, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || IsOnGitHub(uri))
        {
            return false;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            // No token: this is the vendor, not GitHub's API.
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            return false;
        }
    }

    /// <summary>Hosts never download a binary from GitHub (App Installer comes from Kintsugi's own copy for that
    /// reason), so a manifest whose installer is a GitHub release gets no answer here, and the script refuses one too.</summary>
    private static bool IsOnGitHub(Uri uri) =>
        uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
        || uri.Host.EndsWith(".github.com", StringComparison.OrdinalIgnoreCase)
        || uri.Host.EndsWith("githubusercontent.com", StringComparison.OrdinalIgnoreCase);

    private async Task<JsonDocument> GetJsonAsync(string url, string? token, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        using var request = ScriptApprovalGitHubHeaders.Request(HttpMethod.Get, url, token);
        using var response = await _httpClient.SendAsync(request, timeout.Token);
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
    }

    /// <param name="Type">InstallerType, the installer's own or the manifest's.</param>
    /// <param name="ProductCodes">Every ProductCode the installer is known by: its own, its AppsAndFeaturesEntries'
    /// and the manifest's, braced and upper-case.</param>
    public sealed record ManifestInstaller(string? Architecture, string? Type, string? Url, string? Sha256, IReadOnlyList<string> ProductCodes)
    {
        public bool IsMsi =>
            Type is not null ? Type.Equals("msi", StringComparison.OrdinalIgnoreCase) || Type.Equals("wix", StringComparison.OrdinalIgnoreCase)
                : Url?.EndsWith(".msi", StringComparison.OrdinalIgnoreCase) == true;
    }

    /// <summary>
    /// The installers of a winget installer manifest, read line by line. winget-pkgs manifests are machine-written
    /// block YAML in one shape (top-level keys, then an <c>Installers:</c> list of mappings), and only five keys are
    /// read, so this is not a YAML parser and needs none. Pure, so the shape is tested without a network.
    /// </summary>
    public static IReadOnlyList<ManifestInstaller> ParseInstallers(string yaml)
    {
        var top = new Dictionary<string, string>(StringComparer.Ordinal);
        var topCodes = new List<string>();
        var items = new List<(Dictionary<string, string> Fields, List<string> Codes)>();
        var inInstallers = false;
        var itemIndent = -1;
        foreach (var raw in yaml.Split('\n'))
        {
            var line = raw.TrimEnd('\r', ' ', '\t');
            var text = line.TrimStart();
            if (text.Length == 0 || text.StartsWith('#'))
            {
                continue;
            }

            var indent = line.Length - text.Length;
            var dashed = text.StartsWith("- ", StringComparison.Ordinal);
            if (indent == 0 && !dashed)
            {
                inInstallers = text.StartsWith("Installers:", StringComparison.Ordinal);
                itemIndent = -1;
                Collect(text, top, topCodes, itemLevel: true);
                continue;
            }

            var body = dashed ? text[2..].TrimStart() : text;
            if (!inInstallers)
            {
                Collect(body, top, topCodes, itemLevel: false);
                continue;
            }

            if (dashed && (itemIndent < 0 || indent == itemIndent))
            {
                itemIndent = indent;
                items.Add((new Dictionary<string, string>(StringComparer.Ordinal), new List<string>()));
            }

            if (items.Count > 0)
            {
                Collect(body, items[^1].Fields, items[^1].Codes, itemLevel: dashed ? indent == itemIndent : indent == itemIndent + 2);
            }
        }

        return items.Select(item =>
        {
            string? Get(string key) => item.Fields.TryGetValue(key, out var own) ? own : top.TryGetValue(key, out var shared) ? shared : null;
            var sha = Get("InstallerSha256");
            return new ManifestInstaller(
                Get("Architecture"), Get("InstallerType"), Get("InstallerUrl"),
                sha is not null && Sha256Pattern.IsMatch(sha) ? sha : null,
                item.Codes.Concat(topCodes).Distinct(StringComparer.Ordinal).ToList());
        }).ToList();
    }

    private static void Collect(string text, Dictionary<string, string> fields, List<string> codes, bool itemLevel)
    {
        var colon = text.IndexOf(':');
        if (colon <= 0)
        {
            return;
        }

        var key = text[..colon].Trim();
        var value = text[(colon + 1)..].Trim().Trim('\'', '"');
        if (key == "ProductCode")
        {
            if (NormalizeProductCode(value) is { } code)
            {
                codes.Add(code);
            }
        }
        else if (itemLevel && value.Length > 0)
        {
            fields.TryAdd(key, value);
        }
    }

    /// <summary>Braced and upper-case, or null when it is not a GUID: Windows Installer's own form.</summary>
    public static string? NormalizeProductCode(string? value)
    {
        var trimmed = (value ?? "").Trim().Trim('{', '}');
        var braced = "{" + trimmed.ToUpperInvariant() + "}";
        return ProductCodePattern.IsMatch(braced) ? braced : null;
    }
}
