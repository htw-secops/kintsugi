using Kintsugi.Application.UpgradePaths;

namespace Kintsugi.Tests.Application.UpgradePaths;

public class ExitCodeHintsTests
{
    [Theory]
    [InlineData(-1978335138, "0x8A15005E")] // msstore certificate pinning failed
    [InlineData(-2147024809, "0x80070057")] // E_INVALIDARG
    [InlineData(-1978335189, "0x8A15002B")] // no applicable upgrade
    [InlineData(0, "0x00000000")]
    [InlineData(1, "0x00000001")]
    public void ToHex_ReinterpretsANegativeExitCode_AsTheUnsignedValueDocumentsPrint(int code, string expected)
    {
        Assert.Equal(expected, ExitCodeHints.ToHex(code));
    }

    [Theory]
    [InlineData(-1978335138, "0x8A15005E", "msstore certificate pinning failed: outdated winget or SSL inspection")]
    [InlineData(-2147024809, "0x80070057", "Invalid parameter: often an MSIX framework package or an app already current")]
    public void TryExplain_GivesTheHintForAKnownCode(int code, string hex, string hint)
    {
        Assert.True(ExitCodeHints.TryExplain(code, out var actualHex, out var actualHint));
        Assert.Equal(hex, actualHex);
        Assert.Equal(hint, actualHint);

        // The unsigned form, the way documents write it, finds the same entry.
        Assert.True(ExitCodeHints.TryExplain(unchecked((uint)code), out var unsignedHex, out var unsignedHint));
        Assert.Equal(hex, unsignedHex);
        Assert.Equal(hint, unsignedHint);
    }

    [Fact]
    public void TryExplain_StillWritesTheHex_ForACodeItHasNoHintFor()
    {
        Assert.False(ExitCodeHints.TryExplain(-1073741819, out var hex, out var hint)); // 0xC0000005
        Assert.Equal("0xC0000005", hex);
        Assert.Null(hint);
    }

    [Theory]
    [InlineData("winget upgrade exited with -1978335138", "0x8A15005E (msstore certificate pinning failed: outdated winget or SSL inspection)")]
    [InlineData("Installer failed with exit code: 0x80070057", "0x80070057 (Invalid parameter: often an MSIX framework package or an app already current)")]
    [InlineData("failed: 0x8a15005e", "0x8A15005E (msstore certificate pinning failed: outdated winget or SSL inspection)")]
    [InlineData("exit -2147024809 for Microsoft.NET.Native.Runtime.2.2", "0x80070057 (Invalid parameter: often an MSIX framework package or an app already current)")]
    [InlineData("failed with 0xC0000005", "0xC0000005")] // not one it explains: still readable
    [InlineData("failed with -1073741819", "0xC0000005")]
    public void Describe_FindsTheCodeInOutput_WhicheverWayItIsWritten(string output, string expected)
    {
        Assert.Equal(expected, ExitCodeHints.Describe(output));
    }

    [Fact]
    public void Describe_TakesTheFirstCodeInTheText()
    {
        // The first one a script printed is the one that stopped it.
        Assert.StartsWith(
            "0x8A15005E",
            ExitCodeHints.Describe("winget exited with -1978335138, then 0x80070057 followed"));
        Assert.StartsWith(
            "0x80070057",
            ExitCodeHints.Describe("0x80070057 first, then -1978335138"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("nothing numeric happened")]
    [InlineData("exit -1")] // a small negative number is not an HRESULT
    [InlineData("passed --count -5 to the tool")]
    [InlineData("a hash 0x8A15005E00FF is longer than a code")]
    [InlineData("version 1.2.3-4567890 built")] // a hyphen inside a version is not a minus sign
    public void Describe_ReturnsNull_WhenTheTextHoldsNoCode(string? output)
    {
        Assert.Null(ExitCodeHints.Describe(output));
    }
}
