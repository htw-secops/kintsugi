using Kintsugi.Application.OutlookSignatures.Commands.UpdateOutlookSignatureSource;
using Kintsugi.Domain.Entities;
using Kintsugi.Domain.Exceptions;

namespace Kintsugi.Tests.Application.OutlookSignatures;

public class OutlookSignatureSourceSettingsTests
{
    private const string Url = "https://signatures.example.com/bulk";

    [Fact]
    public void Create_KeepsWhatItWasGiven_Trimmed()
    {
        var settings = OutlookSignatureSourceSettings.Create(true, $" {Url} ", " .ex ");

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
        Assert.Throws<DomainException>(() => OutlookSignatureSourceSettings.Create(false, url, null));

        var result = new UpdateOutlookSignatureSourceCommandValidator().Validate(new UpdateOutlookSignatureSourceCommand(false, url, null));
        Assert.Contains(result.Errors, e => e.PropertyName == "EndpointUrl");
    }

    [Theory]
    [InlineData(null)]
    [InlineData(" ")]
    public void EnablingWithoutAnAddress_IsRefused(string? url)
    {
        Assert.Throws<DomainException>(() => OutlookSignatureSourceSettings.Create(true, url, null));
        Assert.False(new UpdateOutlookSignatureSourceCommandValidator().Validate(new UpdateOutlookSignatureSourceCommand(true, url, null)).IsValid);

        // Saved switched off, half a form is fine: it is how the page is filled in.
        Assert.False(OutlookSignatureSourceSettings.Create(false, url, null).Enabled);
    }

    /// <summary>Juan's review of PR #80: the audience is the endpoint URL as stored, never a setting, so no
    /// administrator can mint a token meant for another service.</summary>
    [Fact]
    public void TheTokenAudience_IsTheEndpointUrlExactlyAsStored()
    {
        var settings = OutlookSignatureSourceSettings.Create(true, $"  {Url}  ", null);

        Assert.Equal(Url, settings.EndpointUrl);
        Assert.Equal(Url, settings.TokenAudience);
        Assert.Null(typeof(OutlookSignatureSourceSettings).GetProperty("Audience"));
        Assert.Null(typeof(Kintsugi.Application.OutlookSignatures.OutlookSignatureSourceDto).GetProperty("Audience"));
        Assert.Null(typeof(UpdateOutlookSignatureSourceCommand).GetProperty("Audience"));
    }

    [Theory]
    [InlineData("has space")]
    [InlineData("back\\slash")]
    [InlineData("@example.com")]
    [InlineData("abcdefghijklmnopqrstu")]
    public void ASuffixThatCouldNotBePartOfAWindowsUsername_IsRefused(string suffix)
    {
        Assert.Throws<DomainException>(() => OutlookSignatureSourceSettings.Create(false, Url, suffix));
        Assert.False(new UpdateOutlookSignatureSourceCommandValidator().Validate(new UpdateOutlookSignatureSourceCommand(false, Url, suffix)).IsValid);
    }

    [Fact]
    public void AFailure_KeepsTheLastCount_AndASuccessClearsTheError()
    {
        var settings = OutlookSignatureSourceSettings.Create(true, Url, null);
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
