using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kintsugi.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPatchFailureSignature : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "FailureSignature",
                schema: "patching",
                table: "patch_failures",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_patch_failures_FailureSignature",
                schema: "patching",
                table: "patch_failures",
                column: "FailureSignature");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_patch_failures_FailureSignature",
                schema: "patching",
                table: "patch_failures");

            migrationBuilder.DropColumn(
                name: "FailureSignature",
                schema: "patching",
                table: "patch_failures");
        }
    }
}
