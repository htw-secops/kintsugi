using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kintsugi.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPatchingPolicyBranding : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<byte[]>(
                name: "OrganisationLogoPng",
                schema: "patching",
                table: "patching_policy_settings",
                type: "bytea",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OrganisationName",
                schema: "patching",
                table: "patching_policy_settings",
                type: "character varying(80)",
                maxLength: 80,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "OrganisationLogoPng",
                schema: "patching",
                table: "patching_policy_settings");

            migrationBuilder.DropColumn(
                name: "OrganisationName",
                schema: "patching",
                table: "patching_policy_settings");
        }
    }
}
