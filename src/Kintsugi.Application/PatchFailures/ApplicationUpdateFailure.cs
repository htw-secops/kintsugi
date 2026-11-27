using System.Text.RegularExpressions;
using Kintsugi.Application.UpgradePaths;

namespace Kintsugi.Application.PatchFailures;

/// <summary>
/// Says, in plain words, why a failed <em>application</em> update failed, from the output the agent
/// captured. The operating-system twin is <see cref="OperatingSystemUpdateFailure"/>.
/// </summary>
/// <remarks>
/// Without this every application row showed the same fallback text, whatever the error was. Only a
/// code or message that really identifies the cause gets one here; anything else returns null so the
/// screens keep their honest "read the output" fallback. Adding a case is adding a block below.
/// </remarks>
public static class ApplicationUpdateFailure
{
    // Windows Installer's ERROR_INSTALL_ALREADY_RUNNING, which Chocolatey passes through as its exit code.
    private static readonly Regex InstallerBusy = new(@"exited with 1618(?!\d)", RegexOptions.Compiled);

    // Windows Installer's 1714 ("the older version cannot be removed") caused by 1612 ("the installation source for
    // this product is not available"), as the winget script now copies it out of the installer log. LibreOffice 7.6
    // to 26.8 failed this way: the major upgrade has to uninstall the old version, and Windows' cached copy of the
    // old MSI was gone. winget's own uninstall of the previous version meets the same missing copy as
    // "Uninstall failed with exit code: 1612" (Java 8 Update 491).
    private static readonly Regex OldVersionSourceMissing = new(@"Error 1714\b.*System Error 1612(?!\d)|Uninstall failed with exit code:?\s*1612(?!\d)", RegexOptions.Compiled);

    // The winget script's last line when it removed an application to reinstall it (0x8A150114 or
    // 0x8A15008E) and could not install the new version: the host is now without it.
    private static readonly Regex RemovedAndNotReinstalled = new(@"was uninstalled so that the new version could be installed, and the install failed", RegexOptions.Compiled);

    // What to do, only for the codes whose meaning leaves no doubt. Every other documented code still
    // gets Microsoft's description; a guessed remedy would be worse than none.
    private static readonly Dictionary<uint, string> FixFor = new()
    {
        [0x8A15008E] = "Nothing at first: the winget script removes the old version and installs the new one itself, once it has downloaded and checked the new installer. If this keeps failing, the output below says which step stopped.",
        [0x8A150114] = "Nothing at first: the winget script removes the old version and installs the new one itself, once it has downloaded and checked the new installer. If this keeps failing, the output below says which step stopped.",
        [0x8A150086] = "Check the host's network (proxy, SSL inspection), then patch again.",
        [0x8A150109] = "Restart the host to finish the installation.",
        [0x8A15002B] = "Nothing to do: the application is already current.",
    };

    /// <returns>The cause, or null for an operating-system update or an output nothing recognises.</returns>
    public static OperatingSystemUpdateFailure.Cause? Explain(string applicationName, string? details)
    {
        if (OperatingSystemUpdateFailure.IsOperatingSystem(applicationName) || string.IsNullOrWhiteSpace(details))
        {
            return null;
        }

        if (RemovedAndNotReinstalled.IsMatch(details))
        {
            return new OperatingSystemUpdateFailure.Cause(
                "Removed to reinstall, and the new version did not install",
                "winget could not upgrade this application in place, so the old version was removed to install the new one, and that install failed twice. This host no longer has the application.",
                "Read the output below for why the install failed, fix that, then install the application again: a Required software title for it, or the vendor's installer.");
        }

        if (OldVersionSourceMissing.IsMatch(details))
        {
            return new OperatingSystemUpdateFailure.Cause(
                "The old version's installer is missing",
                "The new version has to remove the old one first, and Windows Installer needs the old version's own installer to do that. Its cached copy is gone from this PC (Windows Installer errors 1714 and 1612), so every upgrade stops here.",
                "Remove the old version on the host with Microsoft's Program Install and Uninstall troubleshooter, or repair it from the old version's original installer, then patch again.");
        }

        if (InstallerBusy.IsMatch(details))
        {
            return new OperatingSystemUpdateFailure.Cause(
                "Another installer was running",
                "Windows allows one installation at a time, and another one (often the previous package in the same patch run) had not finished.",
                "Usually nothing: the agent retries on its own. If it keeps failing, check the host for a stuck msiexec or a pending restart.");
        }

        // winget exits with an HRESULT. A person cannot act on "exited with -1978335138", so say the code
        // the documents print, what winget itself says it means, and what to do where that is clear.
        if (details.Contains("winget", StringComparison.OrdinalIgnoreCase)
            && ExitCodeHints.Describe(details) is { } described)
        {
            var hex = described.Split(' ')[0];
            var code = Convert.ToUInt32(hex[2..], 16);
            ExitCodeHints.TryExplain(code, out _, out var ourHint);
            var documented = WingetReturnCodes.All.GetValueOrDefault(code);

            return new OperatingSystemUpdateFailure.Cause(
                documented is null ? $"winget failed with {hex}" : $"winget: {documented.Title}",
                // Our own hint is the more specific one where both exist (0x8A15005E).
                ourHint ?? (documented is null
                    ? $"winget reported {hex}, which is not in winget's list of documented codes."
                    : $"{documented.Description} (winget code {hex})."),
                FixFor.GetValueOrDefault(code)
                    ?? $"Read the output below for what winget printed before it stopped. Run `winget error {hex}` on the host for Microsoft's own description.");
        }

        return null;
    }
}
