using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kintsugi.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddUpgradePathRepairCandidate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "CandidateDraftedUtc",
                schema: "patching",
                table: "upgrade_paths",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "CandidateFailureId",
                schema: "patching",
                table: "upgrade_paths",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CandidateNotes",
                schema: "patching",
                table: "upgrade_paths",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CandidateScript",
                schema: "patching",
                table: "upgrade_paths",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CandidateDraftedUtc",
                schema: "patching",
                table: "upgrade_paths");

            migrationBuilder.DropColumn(
                name: "CandidateFailureId",
                schema: "patching",
                table: "upgrade_paths");

            migrationBuilder.DropColumn(
                name: "CandidateNotes",
                schema: "patching",
                table: "upgrade_paths");

            migrationBuilder.DropColumn(
                name: "CandidateScript",
                schema: "patching",
                table: "upgrade_paths");
        }
    }
}
