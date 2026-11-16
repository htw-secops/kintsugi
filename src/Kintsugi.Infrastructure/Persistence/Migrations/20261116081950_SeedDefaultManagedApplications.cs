using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kintsugi.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SeedDefaultManagedApplications : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Applications that other tools own, so Kintsugi never gives them a script or an upgrade:
            // Store/MSIX framework packages (updated by the Store together with the apps that use
            // them), endpoint and patching agents, and Microsoft's own installers. Patterns end in
            // "*". These are only what an install starts with: they show on the Managed Elsewhere
            // page and can be removed or reassigned there. Each row is added only if no row with that
            // name exists, so a name an administrator already marked keeps its owner. Plain SQL so
            // the migration does not depend on the entity.
            migrationBuilder.Sql(@"
                INSERT INTO patching.managed_owners (""Id"", ""Name"", ""NormalizedName"", ""CreatedAtUtc"")
                SELECT gen_random_uuid(), 'Microsoft Store', 'microsoft store', now()
                WHERE NOT EXISTS (SELECT 1 FROM patching.managed_owners WHERE ""NormalizedName"" = 'microsoft store');
                INSERT INTO patching.managed_owners (""Id"", ""Name"", ""NormalizedName"", ""CreatedAtUtc"")
                SELECT gen_random_uuid(), 'Microsoft 365', 'microsoft 365', now()
                WHERE NOT EXISTS (SELECT 1 FROM patching.managed_owners WHERE ""NormalizedName"" = 'microsoft 365');
                INSERT INTO patching.managed_owners (""Id"", ""Name"", ""NormalizedName"", ""CreatedAtUtc"")
                SELECT gen_random_uuid(), 'Automox', 'automox', now()
                WHERE NOT EXISTS (SELECT 1 FROM patching.managed_owners WHERE ""NormalizedName"" = 'automox');
                INSERT INTO patching.managed_applications (""Id"", ""ApplicationName"", ""NormalizedName"", ""Owner"", ""CreatedAtUtc"")
                SELECT gen_random_uuid(), 'Microsoft.VCLibs.*', 'microsoft.vclibs.*', 'Microsoft Store', now()
                WHERE NOT EXISTS (SELECT 1 FROM patching.managed_applications WHERE ""NormalizedName"" = 'microsoft.vclibs.*');
                INSERT INTO patching.managed_applications (""Id"", ""ApplicationName"", ""NormalizedName"", ""Owner"", ""CreatedAtUtc"")
                SELECT gen_random_uuid(), 'Microsoft.NET.Native.*', 'microsoft.net.native.*', 'Microsoft Store', now()
                WHERE NOT EXISTS (SELECT 1 FROM patching.managed_applications WHERE ""NormalizedName"" = 'microsoft.net.native.*');
                INSERT INTO patching.managed_applications (""Id"", ""ApplicationName"", ""NormalizedName"", ""Owner"", ""CreatedAtUtc"")
                SELECT gen_random_uuid(), 'Microsoft.UI.Xaml.*', 'microsoft.ui.xaml.*', 'Microsoft Store', now()
                WHERE NOT EXISTS (SELECT 1 FROM patching.managed_applications WHERE ""NormalizedName"" = 'microsoft.ui.xaml.*');
                INSERT INTO patching.managed_applications (""Id"", ""ApplicationName"", ""NormalizedName"", ""Owner"", ""CreatedAtUtc"")
                SELECT gen_random_uuid(), 'Microsoft.WindowsAppRuntime.*', 'microsoft.windowsappruntime.*', 'Microsoft Store', now()
                WHERE NOT EXISTS (SELECT 1 FROM patching.managed_applications WHERE ""NormalizedName"" = 'microsoft.windowsappruntime.*');
                INSERT INTO patching.managed_applications (""Id"", ""ApplicationName"", ""NormalizedName"", ""Owner"", ""CreatedAtUtc"")
                SELECT gen_random_uuid(), 'Microsoft.DesktopAppInstaller', 'microsoft.desktopappinstaller', 'Microsoft Store', now()
                WHERE NOT EXISTS (SELECT 1 FROM patching.managed_applications WHERE ""NormalizedName"" = 'microsoft.desktopappinstaller');
                INSERT INTO patching.managed_applications (""Id"", ""ApplicationName"", ""NormalizedName"", ""Owner"", ""CreatedAtUtc"")
                SELECT gen_random_uuid(), 'Copilot', 'copilot', 'Microsoft Store', now()
                WHERE NOT EXISTS (SELECT 1 FROM patching.managed_applications WHERE ""NormalizedName"" = 'copilot');
                INSERT INTO patching.managed_applications (""Id"", ""ApplicationName"", ""NormalizedName"", ""Owner"", ""CreatedAtUtc"")
                SELECT gen_random_uuid(), 'Microsoft Copilot', 'microsoft copilot', 'Microsoft Store', now()
                WHERE NOT EXISTS (SELECT 1 FROM patching.managed_applications WHERE ""NormalizedName"" = 'microsoft copilot');
                INSERT INTO patching.managed_applications (""Id"", ""ApplicationName"", ""NormalizedName"", ""Owner"", ""CreatedAtUtc"")
                SELECT gen_random_uuid(), 'CrowdStrike*', 'crowdstrike*', 'CrowdStrike', now()
                WHERE NOT EXISTS (SELECT 1 FROM patching.managed_applications WHERE ""NormalizedName"" = 'crowdstrike*');
                INSERT INTO patching.managed_applications (""Id"", ""ApplicationName"", ""NormalizedName"", ""Owner"", ""CreatedAtUtc"")
                SELECT gen_random_uuid(), 'ncplayer', 'ncplayer', 'NinjaOne', now()
                WHERE NOT EXISTS (SELECT 1 FROM patching.managed_applications WHERE ""NormalizedName"" = 'ncplayer');
                INSERT INTO patching.managed_applications (""Id"", ""ApplicationName"", ""NormalizedName"", ""Owner"", ""CreatedAtUtc"")
                SELECT gen_random_uuid(), 'ncstreamer', 'ncstreamer', 'NinjaOne', now()
                WHERE NOT EXISTS (SELECT 1 FROM patching.managed_applications WHERE ""NormalizedName"" = 'ncstreamer');
                INSERT INTO patching.managed_applications (""Id"", ""ApplicationName"", ""NormalizedName"", ""Owner"", ""CreatedAtUtc"")
                SELECT gen_random_uuid(), 'Jamf Self Service+', 'jamf self service+', 'Jamf Pro', now()
                WHERE NOT EXISTS (SELECT 1 FROM patching.managed_applications WHERE ""NormalizedName"" = 'jamf self service+');
                INSERT INTO patching.managed_applications (""Id"", ""ApplicationName"", ""NormalizedName"", ""Owner"", ""CreatedAtUtc"")
                SELECT gen_random_uuid(), 'Microsoft 365 Apps*', 'microsoft 365 apps*', 'Microsoft 365', now()
                WHERE NOT EXISTS (SELECT 1 FROM patching.managed_applications WHERE ""NormalizedName"" = 'microsoft 365 apps*');
                INSERT INTO patching.managed_applications (""Id"", ""ApplicationName"", ""NormalizedName"", ""Owner"", ""CreatedAtUtc"")
                SELECT gen_random_uuid(), 'Automox Agent', 'automox agent', 'Automox', now()
                WHERE NOT EXISTS (SELECT 1 FROM patching.managed_applications WHERE ""NormalizedName"" = 'automox agent');");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                DELETE FROM patching.managed_applications WHERE ""NormalizedName"" IN ('microsoft.vclibs.*','microsoft.net.native.*','microsoft.ui.xaml.*','microsoft.windowsappruntime.*','microsoft.desktopappinstaller','copilot','microsoft copilot','crowdstrike*','ncplayer','ncstreamer','jamf self service+','microsoft 365 apps*','automox agent');");
        }
    }
}
