namespace Kintsugi.Application.Common.Interfaces;

/// <summary>
/// Kintsugi's own copy of the latest App Installer release (winget), which the winget script installs on
/// a host whose Microsoft Store cannot deliver it (see <c>WingetUpgradeScript</c>'s
/// <c>Install-AppInstallerFromKintsugi</c>). The server fetches it from the winget-cli GitHub releases so
/// that hosts never pull binaries from GitHub themselves.
/// </summary>
public interface IAppInstallerMirror
{
    /// <summary>What is on offer now, or null before the first refresh has succeeded.</summary>
    Task<AppInstallerOffer?> GetOfferAsync(CancellationToken cancellationToken);

    /// <summary>One file of the current offer, or null for a name the offer does not list.</summary>
    Task<Stream?> OpenFileAsync(string fileName, CancellationToken cancellationToken);

    /// <summary>
    /// Fetches the latest release when it is newer than the offer. Throws when the release cannot be read or a
    /// file does not match GitHub's own SHA-256; the offer already published stays as it was.
    /// </summary>
    Task<AppInstallerRefreshResult> RefreshAsync(CancellationToken cancellationToken);
}

/// <param name="Role">"bundle", "dependencies" or "license": what the script does with the file.</param>
/// <param name="Sha256">Lowercase hex, checked against GitHub's digest here and again by the host.</param>
public sealed record AppInstallerFile(string Role, string Name, string Sha256, long SizeBytes);

/// <param name="Version">The release tag without its "v" (1.29.380): winget's own version numbering.</param>
public sealed record AppInstallerOffer(string Version, IReadOnlyList<AppInstallerFile> Files, DateTimeOffset MirroredUtc);

public sealed record AppInstallerRefreshResult(AppInstallerOffer Offer, bool Changed);
