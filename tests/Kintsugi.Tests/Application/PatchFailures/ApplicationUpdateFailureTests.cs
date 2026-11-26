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
        Assert.Contains("Uninstall", cause.SuggestedFix);
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
