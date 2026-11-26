using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kintsugi.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddManagedApplicationSource : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Source",
                schema: "patching",
                table: "managed_applications",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "Admin");

            // Parts of another product that have no installer of their own: the Teams add-in for Office (updated by
            // the Teams client) and Windows' application compatibility database (a shim file, not a package).
            // Kintsugi has nothing to patch for them, so it marks them itself, as Auto, for a person to confirm.
            // Only generic Microsoft names go here: this repository is public, so an organisation's own tools are
            // added on the Managed Elsewhere page instead. Each row is added only if no row with that name exists,
            // so a name an administrator already marked keeps its owner and source. Plain SQL so the migration does
            // not depend on the entity.
            migrationBuilder.Sql(@"
                INSERT INTO patching.managed_owners (""Id"", ""Name"", ""NormalizedName"", ""CreatedAtUtc"")
                SELECT gen_random_uuid(), 'Microsoft Teams', 'microsoft teams', now()
                WHERE NOT EXISTS (SELECT 1 FROM patching.managed_owners WHERE ""NormalizedName"" = 'microsoft teams');
                INSERT INTO patching.managed_owners (""Id"", ""Name"", ""NormalizedName"", ""CreatedAtUtc"")
                SELECT gen_random_uuid(), 'Windows', 'windows', now()
                WHERE NOT EXISTS (SELECT 1 FROM patching.managed_owners WHERE ""NormalizedName"" = 'windows');
                INSERT INTO patching.managed_applications (""Id"", ""ApplicationName"", ""NormalizedName"", ""Owner"", ""Source"", ""CreatedAtUtc"")
                SELECT gen_random_uuid(), 'Microsoft Teams Meeting Add-in*', 'microsoft teams meeting add-in*', 'Microsoft Teams', 'Auto', now()
                WHERE NOT EXISTS (SELECT 1 FROM patching.managed_applications WHERE ""NormalizedName"" = 'microsoft teams meeting add-in*');
                INSERT INTO patching.managed_applications (""Id"", ""ApplicationName"", ""NormalizedName"", ""Owner"", ""Source"", ""CreatedAtUtc"")
                SELECT gen_random_uuid(), 'Microsoft Windows Application Compatibility Fix Database*', 'microsoft windows application compatibility fix database*', 'Windows', 'Auto', now()
                WHERE NOT EXISTS (SELECT 1 FROM patching.managed_applications WHERE ""NormalizedName"" = 'microsoft windows application compatibility fix database*');");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Only the rows Up added: the same name and still Auto. A row a person confirmed (now Admin) is theirs and
            // stays. The two owners stay on the list because rows added since may use them.
            migrationBuilder.Sql(@"
                DELETE FROM patching.managed_applications
                WHERE ""Source"" = 'Auto'
                  AND ""NormalizedName"" IN ('microsoft teams meeting add-in*', 'microsoft windows application compatibility fix database*');");

            migrationBuilder.DropColumn(
                name: "Source",
                schema: "patching",
                table: "managed_applications");
        }
    }
}
