using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kintsugi.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddHostMaintenance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "MaintenanceReason",
                schema: "patching",
                table: "hosts",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "MaintenanceSinceUtc",
                schema: "patching",
                table: "hosts",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "MaintenanceUntilUtc",
                schema: "patching",
                table: "hosts",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MaintenanceReason",
                schema: "patching",
                table: "hosts");

            migrationBuilder.DropColumn(
                name: "MaintenanceSinceUtc",
                schema: "patching",
                table: "hosts");

            migrationBuilder.DropColumn(
                name: "MaintenanceUntilUtc",
                schema: "patching",
                table: "hosts");
        }
    }
}
