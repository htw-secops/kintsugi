namespace Kintsugi.Application.Common.Interfaces;

/// <summary>
/// Finds where an MSI that winget no longer lists can still be downloaded, from the manifest winget-pkgs held for
/// it before the version was removed. The winget script asks for it (<c>WingetUpgradeScript</c>'s
/// <c>Save-RetiredMsiFromKintsugi</c>) when an installed version's cached MSI is gone and <c>winget download</c>
/// of that version fails, because putting that MSI back in Windows Installer's cache is the only repair that
/// lets the upgrade remove the old version without uninstalling anything.
/// </summary>
public interface IRetiredWingetInstallerLookup
{
    /// <summary>
    /// The installer whose manifest names <paramref name="productCode"/>, for <paramref name="version"/> of
    /// <paramref name="packageId"/> or a package filed under it (<c>TheDocumentFoundation.LibreOffice.LTS</c> for
    /// <c>TheDocumentFoundation.LibreOffice</c>). Null when winget-pkgs never held one, or no address for it answers.
    /// Throws when GitHub cannot be read, so an outage is not reported as "none".
    /// </summary>
    Task<RetiredWingetInstaller?> FindAsync(string packageId, string version, string productCode, CancellationToken cancellationToken);
}

/// <param name="PackageIdentifier">The winget id the manifest was filed under, which may be a sub-package of the
/// one asked for.</param>
/// <param name="Url">An address that answered when it was looked up: the manifest's own, or a vendor archive's.</param>
/// <param name="Sha256">The manifest's InstallerSha256, lowercase. The host checks the download against it.</param>
/// <param name="ManifestCommit">The winget-pkgs commit the manifest was read at, so the answer can be checked.</param>
public sealed record RetiredWingetInstaller(
    string PackageIdentifier, string Version, string ProductCode, string Url, string Sha256, string ManifestCommit);
