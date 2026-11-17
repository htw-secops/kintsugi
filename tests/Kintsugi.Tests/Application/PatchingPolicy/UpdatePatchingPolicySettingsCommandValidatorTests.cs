using FluentValidation.TestHelper;
using Kintsugi.Application.PatchingPolicy.Commands.UpdatePatchingPolicySettings;
using Kintsugi.Domain.Entities;
using Kintsugi.Tests.Domain;
using Kintsugi.Domain.Enums;

namespace Kintsugi.Tests.Application.PatchingPolicy;

public class UpdatePatchingPolicySettingsCommandValidatorTests
{
    private readonly UpdatePatchingPolicySettingsCommandValidator _validator = new();

    [Fact]
    public void ValidCommand_PassesValidation()
    {
        var result = _validator.TestValidate(new UpdatePatchingPolicySettingsCommand(7, PatchingTimeUnit.Days, 1, PatchingTimeUnit.Days, 3));

        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void IntervalValue_BelowOne_IsRejected()
    {
        var result = _validator.TestValidate(new UpdatePatchingPolicySettingsCommand(0, PatchingTimeUnit.Days, 1, PatchingTimeUnit.Days, 3));

        result.ShouldHaveValidationErrorFor(c => c.IntervalValue);
    }

    [Fact]
    public void DelayValue_BelowOne_IsRejected()
    {
        var result = _validator.TestValidate(new UpdatePatchingPolicySettingsCommand(7, PatchingTimeUnit.Days, 0, PatchingTimeUnit.Days, 3));

        result.ShouldHaveValidationErrorFor(c => c.DelayValue);
    }

    [Fact]
    public void MaxDelayCount_Negative_IsRejected()
    {
        var result = _validator.TestValidate(new UpdatePatchingPolicySettingsCommand(7, PatchingTimeUnit.Days, 1, PatchingTimeUnit.Days, -1));

        result.ShouldHaveValidationErrorFor(c => c.MaxDelayCount);
    }

    [Fact]
    public void MaxDelayCount_Zero_IsAccepted()
    {
        var result = _validator.TestValidate(new UpdatePatchingPolicySettingsCommand(7, PatchingTimeUnit.Days, 1, PatchingTimeUnit.Days, 0));

        result.ShouldNotHaveValidationErrorFor(c => c.MaxDelayCount);
    }

    private static UpdatePatchingPolicySettingsCommand WithBranding(string? name, byte[]? logo) =>
        new(7, PatchingTimeUnit.Days, 1, PatchingTimeUnit.Days, 3, name, logo);

    [Fact]
    public void Branding_ValidNameAndPng_PassesValidation()
    {
        var result = _validator.TestValidate(WithBranding("Example Corp IT", PatchingPolicySettingsTests.Png()));

        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void Branding_Absent_PassesValidation()
    {
        var result = _validator.TestValidate(WithBranding(null, null));

        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void OrganisationName_OverTheLimit_IsRejected()
    {
        var result = _validator.TestValidate(WithBranding(new string('a', PatchingPolicySettings.MaxOrganisationNameLength + 1), null));

        result.ShouldHaveValidationErrorFor(c => c.OrganisationName);
    }

    [Fact]
    public void OrganisationName_AtTheLimitOnceTrimmed_IsAccepted()
    {
        var result = _validator.TestValidate(WithBranding("  " + new string('a', PatchingPolicySettings.MaxOrganisationNameLength) + "  ", null));

        result.ShouldNotHaveValidationErrorFor(c => c.OrganisationName);
    }

    [Fact]
    public void OrganisationLogoPng_NotAPng_IsRejected()
    {
        var gif = "GIF89a"u8.ToArray();

        var result = _validator.TestValidate(WithBranding(null, gif));

        result.ShouldHaveValidationErrorFor(c => c.OrganisationLogoPng);
    }

    [Fact]
    public void OrganisationLogoPng_TooLarge_IsRejected()
    {
        var result = _validator.TestValidate(WithBranding(null, PatchingPolicySettingsTests.Png(PatchingPolicySettings.MaxOrganisationLogoBytes + 1)));

        result.ShouldHaveValidationErrorFor(c => c.OrganisationLogoPng);
    }
}
