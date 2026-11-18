using Kintsugi.Application.OutlookSignatures.Commands.UpdateOutlookSignatureSource;
using Kintsugi.Domain.Entities;
using Kintsugi.Domain.Exceptions;

namespace Kintsugi.Tests.Application.OutlookSignatures;

public class OutlookSignatureSourceSettingsTests
{
    private const string Url = "https://signatures.example.com/bulk";
    private const string Audience = "https://signatures.example.com";

    [Fact]
    public void Create_KeepsWhatItWasGiven_Trimmed()
    {
        var settings = OutlookSignatureSourceSettings.Create(true, $" {Url} ", Audience, " .ex ");

        Assert.True(settings.Enabled);
        Assert.Equal(Url, settings.EndpointUrl);
        Assert.Equal(".ex", settings.WindowsUsernameSuffix);
        Assert.Null(settings.LastPullStatus);
    }

    /// <summary>The ID token travels in the request's Authorization header, so only https will do.</summary>
    [Theory]
    [InlineData("http://signatures.example.com/bulk")]
    [InlineData("ftp://signatures.example.com/bulk")]
    [InlineData("signatures.example.com/bulk")]
    [InlineData("https://user:pass@signatures.example.com/bulk")]
    public void AnythingButAnHttpsUrl_IsRefused(string url)
    {
        Assert.Throws<DomainException>(() => OutlookSignatureSourceSettings.Create(false, url, Audience, null));

        var result = new UpdateOutlookSignatureSourceCommandValidator().Validate(new UpdateOutlookSignatureSourceCommand(false, url, Audience, null));
        Assert.Contains(result.Errors, e => e.PropertyName == "EndpointUrl");
    }

    [Theory]
    [InlineData(null, Audience)]
    [InlineData(Url, null)]
    [InlineData(" ", " ")]
    public void EnablingWithoutAnAddressAndAnAudience_IsRefused(string? url, string? audience)
    {
        Assert.Throws<DomainException>(() => OutlookSignatureSourceSettings.Create(true, url, audience, null));
        Assert.False(new UpdateOutlookSignatureSourceCommandValidator().Validate(new UpdateOutlookSignatureSourceCommand(true, url, audience, null)).IsValid);

        // Saved switched off, half a form is fine: it is how the page is filled in.
        Assert.False(OutlookSignatureSourceSettings.Create(false, url, audience, null).Enabled);
    }

    [Theory]
    [InlineData("has space")]
    [InlineData("back\\slash")]
    [InlineData("@example.com")]
    [InlineData("abcdefghijklmnopqrstu")]
    public void ASuffixThatCouldNotBePartOfAWindowsUsername_IsRefused(string suffix)
    {
        Assert.Throws<DomainException>(() => OutlookSignatureSourceSettings.Create(false, Url, Audience, suffix));
        Assert.False(new UpdateOutlookSignatureSourceCommandValidator().Validate(new UpdateOutlookSignatureSourceCommand(false, Url, Audience, suffix)).IsValid);
    }

    [Fact]
    public void AFailure_KeepsTheLastCount_AndASuccessClearsTheError()
    {
        var settings = OutlookSignatureSourceSettings.Create(true, Url, Audience, null);
        settings.RecordPull(OutlookSignatureSourceSettings.StatusOk, 3, DateTimeOffset.UtcNow);

        settings.RecordPullFailure(new string('x', 5000), DateTimeOffset.UtcNow);
        Assert.Equal("error", settings.LastPullStatus);
        Assert.Equal(3, settings.LastPullCount);
        Assert.Equal(OutlookSignatureSourceSettings.MaxErrorLength, settings.LastError!.Length);

        settings.RecordPull(OutlookSignatureSourceSettings.StatusDisabled, 0, DateTimeOffset.UtcNow);
        Assert.Null(settings.LastError);
        Assert.Equal(0, settings.LastPullCount);
    }
}
