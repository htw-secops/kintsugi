using Kintsugi.Domain.Entities;
using Kintsugi.Domain.Enums;
using Kintsugi.Domain.Exceptions;

namespace Kintsugi.Tests.Domain;

public class PatchingPolicySettingsTests
{
    [Fact]
    public void Create_SetsAllFields()
    {
        var settings = PatchingPolicySettings.Create(7, PatchingTimeUnit.Days, 1, PatchingTimeUnit.Days, 3);

        Assert.Equal(7, settings.IntervalValue);
        Assert.Equal(PatchingTimeUnit.Days, settings.IntervalUnit);
        Assert.Equal(1, settings.DelayValue);
        Assert.Equal(PatchingTimeUnit.Days, settings.DelayUnit);
        Assert.Equal(3, settings.MaxDelayCount);
    }

    [Fact]
    public void Create_RejectsAnIntervalValueBelowOne()
    {
        Assert.Throws<DomainException>(() => PatchingPolicySettings.Create(0, PatchingTimeUnit.Days, 1, PatchingTimeUnit.Days, 0));
    }

    [Fact]
    public void Create_RejectsADelayValueBelowOne()
    {
        Assert.Throws<DomainException>(() => PatchingPolicySettings.Create(7, PatchingTimeUnit.Days, 0, PatchingTimeUnit.Days, 0));
    }

    [Fact]
    public void Create_RejectsANegativeMaxDelayCount()
    {
        Assert.Throws<DomainException>(() => PatchingPolicySettings.Create(7, PatchingTimeUnit.Days, 1, PatchingTimeUnit.Days, -1));
    }

    [Fact]
    public void Create_AllowsAZeroMaxDelayCount_MeaningNoDeferralPermitted()
    {
        var settings = PatchingPolicySettings.Create(7, PatchingTimeUnit.Days, 1, PatchingTimeUnit.Days, 0);

        Assert.Equal(0, settings.MaxDelayCount);
    }

    [Fact]
    public void Update_ReplacesAllFields()
    {
        var settings = PatchingPolicySettings.Create(7, PatchingTimeUnit.Days, 1, PatchingTimeUnit.Days, 3);

        settings.Update(24, PatchingTimeUnit.Hours, 4, PatchingTimeUnit.Hours, 5);

        Assert.Equal(24, settings.IntervalValue);
        Assert.Equal(PatchingTimeUnit.Hours, settings.IntervalUnit);
        Assert.Equal(4, settings.DelayValue);
        Assert.Equal(PatchingTimeUnit.Hours, settings.DelayUnit);
        Assert.Equal(5, settings.MaxDelayCount);
    }

    [Fact]
    public void Update_RejectsAnIntervalValueBelowOne_LeavingExistingValuesUnchanged()
    {
        var settings = PatchingPolicySettings.Create(7, PatchingTimeUnit.Days, 1, PatchingTimeUnit.Days, 3);

        Assert.Throws<DomainException>(() => settings.Update(0, PatchingTimeUnit.Days, 1, PatchingTimeUnit.Days, 3));
        Assert.Equal(7, settings.IntervalValue);
    }

    // The PNG signature followed by a little padding: the domain checks the signature, not that
    // the rest decodes, so this is all a test needs.
    internal static byte[] Png(int length = 16)
    {
        var bytes = new byte[length];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(bytes, 0);
        return bytes;
    }

    private static PatchingPolicySettings Default() => PatchingPolicySettings.Create(7, PatchingTimeUnit.Days, 1, PatchingTimeUnit.Days, 3);

    [Fact]
    public void Create_HasNoBranding()
    {
        var settings = Default();

        Assert.Null(settings.OrganisationName);
        Assert.Null(settings.OrganisationLogoPng);
    }

    [Fact]
    public void UpdateBranding_TrimsTheNameAndKeepsTheLogo()
    {
        var settings = Default();
        var logo = Png();

        settings.UpdateBranding("  Example Corp IT  ", logo);

        Assert.Equal("Example Corp IT", settings.OrganisationName);
        Assert.Equal(logo, settings.OrganisationLogoPng);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void UpdateBranding_TreatsABlankNameAsNone(string? name)
    {
        var settings = Default();
        settings.UpdateBranding("Example Corp IT", null);

        settings.UpdateBranding(name, null);

        Assert.Null(settings.OrganisationName);
    }

    [Fact]
    public void UpdateBranding_TreatsAnEmptyLogoAsNone()
    {
        var settings = Default();
        settings.UpdateBranding(null, Png());

        settings.UpdateBranding(null, []);

        Assert.Null(settings.OrganisationLogoPng);
    }

    [Fact]
    public void UpdateBranding_AcceptsANameAtTheLimit_MeasuredAfterTrimming()
    {
        var settings = Default();

        settings.UpdateBranding(" " + new string('a', PatchingPolicySettings.MaxOrganisationNameLength) + " ", null);

        Assert.Equal(PatchingPolicySettings.MaxOrganisationNameLength, settings.OrganisationName!.Length);
    }

    [Fact]
    public void UpdateBranding_RejectsANameOverTheLimit_LeavingExistingValuesUnchanged()
    {
        var settings = Default();
        settings.UpdateBranding("Example Corp IT", null);

        Assert.Throws<DomainException>(() => settings.UpdateBranding(new string('a', PatchingPolicySettings.MaxOrganisationNameLength + 1), null));
        Assert.Equal("Example Corp IT", settings.OrganisationName);
    }

    [Fact]
    public void UpdateBranding_RejectsALogoThatIsNotAPng()
    {
        var settings = Default();
        var jpeg = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46 };

        Assert.Throws<DomainException>(() => settings.UpdateBranding(null, jpeg));
        Assert.Null(settings.OrganisationLogoPng);
    }

    [Fact]
    public void UpdateBranding_RejectsALogoShorterThanTheSignature()
    {
        Assert.Throws<DomainException>(() => Default().UpdateBranding(null, [0x89, 0x50, 0x4E]));
    }

    [Fact]
    public void UpdateBranding_AcceptsALogoAtTheLimit_AndRejectsOneByteOver()
    {
        var settings = Default();

        settings.UpdateBranding(null, Png(PatchingPolicySettings.MaxOrganisationLogoBytes));
        Assert.Equal(PatchingPolicySettings.MaxOrganisationLogoBytes, settings.OrganisationLogoPng!.Length);

        Assert.Throws<DomainException>(() => settings.UpdateBranding(null, Png(PatchingPolicySettings.MaxOrganisationLogoBytes + 1)));
    }
}
