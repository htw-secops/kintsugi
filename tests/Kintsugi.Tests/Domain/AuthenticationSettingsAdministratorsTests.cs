using Kintsugi.Domain.Entities;
using Kintsugi.Domain.Enums;

namespace Kintsugi.Tests.Domain;

/// <summary>Who added each administrator and when — the record the Administrators page shows.</summary>
public class AuthenticationSettingsAdministratorsTests
{
    private static readonly DateTimeOffset Then = new(2026, 11, 2, 9, 14, 0, TimeSpan.Zero);

    private static AuthenticationSettings Settings(params string[] admins) =>
        AuthenticationSettings.Create(AuthProvider.GoogleWorkspace, "client-id", "secret", null, null, null, true, admins);

    [Fact]
    public void AddAdmins_RecordsWhoAndWhen_NormalisingBoth()
    {
        var settings = Settings();

        settings.AddAdmins(new[] { " Jake.Uren@HTW.com.au " }, " Juan.Roman@htw.com.au ", Then);

        Assert.Equal(new[] { "jake.uren@htw.com.au" }, settings.AdminEmails);
        var record = Assert.Single(settings.AdminRecords());
        Assert.Equal(("jake.uren@htw.com.au", "juan.roman@htw.com.au", (DateTimeOffset?)Then), record);
    }

    [Fact]
    public void AddAdmins_OfSomeoneAlreadyListed_KeepsTheOriginalRecord()
    {
        var settings = Settings();
        settings.AddAdmins(new[] { "jake@example.invalid" }, "first@example.invalid", Then);

        settings.AddAdmins(new[] { "JAKE@example.invalid" }, "second@example.invalid", Then.AddDays(1));

        var record = Assert.Single(settings.AdminRecords());
        Assert.Equal("first@example.invalid", record.AddedBy);
        Assert.Equal(Then, record.AddedUtc);
    }

    [Fact]
    public void AddAdmins_IgnoresBlanksAndNulls()
    {
        var settings = Settings();

        settings.AddAdmins(new string?[] { null, "  ", "a@example.invalid" }, null, Then);

        Assert.Equal(new[] { "a@example.invalid" }, settings.AdminEmails);
    }

    [Fact]
    public void RemoveAdmin_DropsTheEntryAndItsRecord_CaseInsensitively()
    {
        var settings = Settings();
        settings.AddAdmins(new[] { "a@example.invalid", "b@example.invalid" }, "x@example.invalid", Then);

        settings.RemoveAdmin("A@Example.invalid");

        Assert.Equal(new[] { "b@example.invalid" }, settings.AdminEmails);
        Assert.Equal("b@example.invalid", Assert.Single(settings.AdminAdditions).Email);
    }

    [Fact]
    public void RemoveAdmin_OfSomeoneNotListed_ChangesNothing()
    {
        var settings = Settings("a@example.invalid");

        settings.RemoveAdmin("nobody@example.invalid");
        settings.RemoveAdmin(null);

        Assert.Equal(new[] { "a@example.invalid" }, settings.AdminEmails);
    }

    [Fact]
    public void AdminRecords_ForAnEmailListedBeforeTracking_HasNoAdderAndNoDate()
    {
        // Settings() goes through the Authentication screen's path, but a row migrated from before
        // this existed has an email and no addition at all; build that state directly.
        var settings = Settings();
        typeof(AuthenticationSettings).GetProperty(nameof(AuthenticationSettings.AdminEmails))!
            .SetValue(settings, new List<string> { "old@example.invalid" });

        var record = Assert.Single(settings.AdminRecords());

        Assert.Equal("old@example.invalid", record.Email);
        Assert.Null(record.AddedBy);
        Assert.Null(record.AddedUtc);
    }

    [Fact]
    public void Update_WithNewAddresses_RecordsThemWithNoAdder_AndKeepsExistingRecords()
    {
        var settings = Settings();
        settings.AddAdmins(new[] { "kept@example.invalid", "dropped@example.invalid" }, "juan@example.invalid", Then);

        settings.Update(AuthProvider.GoogleWorkspace, "client-id", null, null, null, null, true,
            new[] { "kept@example.invalid", "typed@example.invalid" });

        var records = settings.AdminRecords();
        Assert.Equal(new[] { "kept@example.invalid", "typed@example.invalid" }, records.Select(r => r.Email));
        Assert.Equal("juan@example.invalid", records[0].AddedBy);
        Assert.Null(records[1].AddedBy);
        Assert.NotNull(records[1].AddedUtc);
        Assert.DoesNotContain(settings.AdminAdditions, a => a.Email == "dropped@example.invalid");
    }

    [Fact]
    public void Update_WithNullList_LeavesRecordsAlone()
    {
        var settings = Settings();
        settings.AddAdmins(new[] { "a@example.invalid" }, "juan@example.invalid", Then);

        settings.Update(AuthProvider.GoogleWorkspace, "client-id", null, null, null, null, true, adminEmails: null);

        Assert.Equal("juan@example.invalid", Assert.Single(settings.AdminRecords()).AddedBy);
    }
}
