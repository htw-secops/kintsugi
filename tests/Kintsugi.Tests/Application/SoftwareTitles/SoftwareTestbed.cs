using Microsoft.EntityFrameworkCore;
using Kintsugi.Application.UpgradePaths;
using Kintsugi.Domain.Entities;
using Kintsugi.Domain.Enums;
using Kintsugi.Infrastructure.Persistence;
using Kintsugi.Infrastructure.Persistence.Repositories;

namespace Kintsugi.Tests.Application.SoftwareTitles;

/// <summary>
/// An InMemory database holding a Windows host, plus helpers to give it inventory, scripts, titles and
/// secrets — what the work-list and Software-screen tests share. The real repositories are used rather
/// than mocks so the handler's reads are the reads production does.
/// </summary>
internal sealed class SoftwareTestbed
{
    public const string ActionScript = "# --appName --appId --update-version --update --install --uninstall";
    public const string UpgradeOnlyScript = "# --appName --appId --update-version --update";

    public ApplicationDbContext Context { get; } = new(new DbContextOptionsBuilder<ApplicationDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    public Host Host { get; }

    public SoftwareTestbed(string operatingSystem = "Windows 11 Pro")
    {
        Host = new Host("pc-1", "SERIAL-1", operatingSystem);
        Context.Hosts.Add(Host);
        Context.SaveChanges();
    }

    public UpgradePathRepository UpgradePaths => new(Context);
    public HostRepository Hosts => new(Context);
    public InstalledApplicationRepository Installed => new(Context);
    public SoftwareTitleRepository Titles => new(Context);
    public ScriptSecretRepository Secrets => new(Context);
    public PatchFailureRepository Failures => new(Context);

    public InstalledApplication Install(string name, string? identifier = null, Host? host = null)
    {
        var app = new InstalledApplication((host ?? Host).Id, name, "1.0", identifier);
        Context.InstalledApplications.Add(app);
        Context.SaveChanges();
        return app;
    }

    /// <summary>A package managed by <paramref name="manager"/>, linked to the manager's own row the way
    /// RegisterApplicationsCommandHandler links them.</summary>
    public void InstallManaged(string manager, string name, string identifier)
    {
        var parent = Context.InstalledApplications.Local.FirstOrDefault(a => a.HostId == Host.Id && a.Name == manager)
            ?? Install(manager);
        var app = new InstalledApplication(Host.Id, name, "1.0", identifier);
        app.SetParent(parent.Id);
        Context.InstalledApplications.Add(app);
        Context.SaveChanges();
    }

    public UpgradePath Script(string name, string platform, string script, bool signed, string? latestVersion = null)
    {
        var path = UpgradePath.Create(name, platform, UpgradePathStatus.Found, latestVersion, UpgradeMethod.Script, null, null, null, null, null, script);
        if (signed)
        {
            path.SignScript("script-sig");
        }

        Context.UpgradePaths.Add(path);
        Context.SaveChanges();
        return path;
    }

    public SoftwareTitle WingetTitle(SoftwareMode mode, params string[] secrets) =>
        AddTitle(SoftwareTitle.Create("Greenshot", PlatformBucket.ForPackageManager("winget"), "winget", "Windows", "Greenshot.Greenshot", null, mode, secrets, "admin"));

    public SoftwareTitle CustomTitle(SoftwareMode mode, string name = "Zoho Assist", string? detectAs = null, params string[] secrets) =>
        AddTitle(SoftwareTitle.Create(name, "Windows", null, "Windows", null, detectAs, mode, secrets, "admin"));

    public SoftwareTitle AddTitle(SoftwareTitle title, bool assign = true, bool sign = true)
    {
        if (assign)
        {
            title.Assign(false, [Host.Id], "admin");
        }

        if (sign)
        {
            title.SetActionSignature("action-sig:" + title.CanonicalActionText());
        }

        Context.SoftwareTitles.Add(title);
        Context.SaveChanges();
        return title;
    }

    public ScriptSecret Secret(string name, string value = "TEST_SECRET_VALUE")
    {
        var secret = ScriptSecret.Create(name, value, "secret-sig", "admin");
        Context.ScriptSecrets.Add(secret);
        Context.SaveChanges();
        return secret;
    }
}
