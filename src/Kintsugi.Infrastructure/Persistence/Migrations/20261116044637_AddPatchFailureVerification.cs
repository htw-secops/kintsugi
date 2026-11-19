using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kintsugi.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPatchFailureVerification : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "FailureCategory",
                schema: "patching",
                table: "patch_failures",
                type: "character varying(48)",
                maxLength: 48,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LastVerifiedUtc",
                schema: "patching",
                table: "patch_failures",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "FailureCategory",
                schema: "patching",
                table: "patch_failures");

            migrationBuilder.DropColumn(
                name: "LastVerifiedUtc",
                schema: "patching",
                table: "patch_failures");
        }
    }
}
