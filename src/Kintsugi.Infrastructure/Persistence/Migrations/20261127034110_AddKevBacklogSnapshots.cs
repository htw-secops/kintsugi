using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kintsugi.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddKevBacklogSnapshots : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "kev_backlog_snapshots",
                schema: "patching",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SnapshotDateUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    DevicesOpen = table.Column<int>(type: "integer", nullable: false),
                    DevicesWithinTarget = table.Column<int>(type: "integer", nullable: false),
                    DevicesOverdue = table.Column<int>(type: "integer", nullable: false),
                    DevicesDueWithin7Days = table.Column<int>(type: "integer", nullable: false),
                    OverdueNotSeen7Days = table.Column<int>(type: "integer", nullable: false),
                    TopCveId = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    TopCveDevices = table.Column<int>(type: "integer", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_kev_backlog_snapshots", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_kev_backlog_snapshots_SnapshotDateUtc",
                schema: "patching",
                table: "kev_backlog_snapshots",
                column: "SnapshotDateUtc",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "kev_backlog_snapshots",
                schema: "patching");
        }
    }
}
