using Kintsugi.Application.PatchFailures;

namespace Kintsugi.Tests.Application.PatchFailures;

public class ApplicationUpdateFailureTests
{
    [Fact]
    public void Chocolatey1618_IsSaidToBeAnotherInstallerRunning()
    {
        var cause = ApplicationUpdateFailure.Explain("python314", "exited with exit code: 1: choco upgrade exited with 1618");

        Assert.Equal("Another installer was running", cause!.Title);
    }

    /// What HTWHPBCD5120MTY's LibreOffice 7.6 to 26.8 upgrade logged, as the winget script now reports it.
    [Fact]
    public void AnOldVersionWhoseCachedInstallerIsGone_IsSaidSo_BeforeTheWingetCode()
    {
        var cause = ApplicationUpdateFailure.Explain(
            "LibreOffice 7.6.7.2",
            "Installer failed with exit code: 1603\n"
            + "MSI (s) (C4:3C) [08:38:06:512]: Product: LibreOffice 26.8.3.1 -- Error 1714. The older version of LibreOffice 26.8.3.1 cannot be removed.  Contact your technical support group.  System Error 1612.\n"
            + "exited with exit code: 1: winget upgrade exited with 0x8A150049; the installer itself exited with 1603");

        Assert.Equal("The old version's installer is missing", cause!.Title);
        Assert.Contains("1714 and 1612", cause.Explanation);
    }

    [Fact]
    public void TheInstallerItselfExiting1618_IsAnotherInstallerRunning()
    {
        var cause = ApplicationUpdateFailure.Explain("App", "winget upgrade exited with 0x8A150049; the installer itself exited with 1618");

        Assert.Equal("Another installer was running", cause!.Title);
    }

    [Fact]
    public void ACodeContainingTheDigits1618_IsNotTakenForIt()
    {
        Assert.Null(ApplicationUpdateFailure.Explain("App", "choco upgrade exited with 16180"));
    }

    [Fact]
    public void ADocumentedWingetCode_SaysWhatWingetSaysAndWhatToDo()
    {
        var cause = ApplicationUpdateFailure.Explain("Bing Wallpaper", "exited with exit code: 1: winget upgrade exited with 0x8A15008E");

        Assert.Equal("winget: Update install technology mismatch", cause!.Title);
        Assert.Contains("different install technology", cause.Explanation);
        Assert.Contains("removes the old version and installs the new one itself", cause.SuggestedFix);
    }

    /// The winget script now reinstalls on 0x8A150114 itself, so the screen does not send anyone to do it by hand.
    [Fact]
    public void AnInstallerThatCannotUpgradeInPlace_IsLeftToTheScriptsReinstall()
    {
        var cause = ApplicationUpdateFailure.Explain("PDF Architect 9", "exited with exit code: 1: winget upgrade exited with 0x8A150114");

        Assert.Equal("winget: Install upgrade not supported", cause!.Title);
        Assert.Contains("removes the old version and installs the new one itself", cause.SuggestedFix);
    }

    /// Java 8 Update 491: the manifest uninstalls the previous version first, and that uninstall met the missing MSI.
    [Fact]
    public void AnUninstallOfThePreviousVersionFailingWith1612_IsTheMissingInstaller()
    {
        var cause = ApplicationUpdateFailure.Explain("Java 8 Update 491 (64-bit)", "exited with exit code: 1: Uninstall failed with exit code: 1612 winget upgrade exited with 0x8A150030");

        Assert.Equal("The old version's installer is missing", cause!.Title);
        Assert.Null(ApplicationUpdateFailure.Explain("App", "Uninstall failed with exit code: 16120"));
    }

    /// The reinstall removed the application and could not install the new version: the row must say the host is without it.
    [Fact]
    public void AReinstallThatLeftTheHostWithoutTheApplication_SaysSo()
    {
        var cause = ApplicationUpdateFailure.Explain(
            "PDF Architect 9",
            "Installer failed with exit code: 1603\n"
            + "PDF Architect 9 was uninstalled so that the new version could be installed, and the install failed twice (last: winget install exited with 0x8A150011): this host no longer has PDF Architect 9");

        Assert.Equal("Removed to reinstall, and the new version did not install", cause!.Title);
        Assert.Contains("no longer has the application", cause.Explanation);
    }

    [Fact]
    public void ADocumentedCodeWithNoCertainRemedy_GetsTheDescriptionAndHowToLookItUp()
    {
        // 0x8A150004: "Opening manifest failed" - documented, but no one remedy fits every cause.
        var cause = ApplicationUpdateFailure.Explain("App", "winget upgrade exited with 0x8A150004");

        Assert.Equal("winget: Manifest failed", cause!.Title);
        Assert.Contains("Opening manifest failed", cause.Explanation);
        Assert.Contains("winget error 0x8A150004", cause.SuggestedFix);
    }

    [Fact]
    public void ACodeWingetDoesNotDocument_NamesItAndSaysSo()
    {
        var cause = ApplicationUpdateFailure.Explain("App", "winget upgrade exited with 0x8A15FFFF");

        Assert.Equal("winget failed with 0x8A15FFFF", cause!.Title);
        Assert.Contains("not in winget's list", cause.Explanation);
    }

    [Fact]
    public void TheDocumentedTable_IsWholeAndEveryEntryIsUsable()
    {
        Assert.True(WingetReturnCodes.All.Count > 150);
        Assert.All(WingetReturnCodes.All, entry =>
        {
            Assert.Equal(0x8A150000u, entry.Key & 0xFFFF0000u);
            Assert.False(string.IsNullOrWhiteSpace(entry.Value.Title));
            Assert.False(string.IsNullOrWhiteSpace(entry.Value.Description));
        });
    }

    [Fact]
    public void AKnownWingetCode_KeepsTheMoreSpecificHintOfOurOwn()
    {
        // -1978335138 is how PowerShell prints 0x8A15005E.
        var cause = ApplicationUpdateFailure.Explain("winget", "winget upgrade exited with -1978335138");

        Assert.Equal("winget: Pinned certificate mismatch", cause!.Title);
        Assert.Contains("certificate pinning", cause.Explanation);
    }

    [Theory]
    [InlineData("Greenshot", "")]
    [InlineData("Greenshot", "something went wrong")]
    [InlineData("Windows", "choco upgrade exited with 1618")] // an operating-system row is explained elsewhere
    public void Output_NothingRecognises_GetsNoCause(string application, string details)
    {
        Assert.Null(ApplicationUpdateFailure.Explain(application, details));
    }
}
