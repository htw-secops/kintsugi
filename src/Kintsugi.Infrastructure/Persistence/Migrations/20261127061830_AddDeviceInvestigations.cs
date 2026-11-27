using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kintsugi.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddDeviceInvestigations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "device_investigation_settings",
                schema: "patching",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    SlackWebhookUrl = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    ConsoleBaseUrl = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_device_investigation_settings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "device_investigations",
                schema: "patching",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    HostId = table.Column<Guid>(type: "uuid", nullable: false),
                    OpenedUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Why = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    FatalErrorsAtOpen = table.Column<int>(type: "integer", nullable: false),
                    LogCollectionId = table.Column<Guid>(type: "uuid", nullable: true),
                    ReportedUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LikelyCause = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    Report = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: true),
                    WrittenByAi = table.Column<bool>(type: "boolean", nullable: false),
                    AlertedUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    AlertError = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    AlertAttempts = table.Column<int>(type: "integer", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_device_investigations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_device_investigations_hosts_HostId",
                        column: x => x.HostId,
                        principalSchema: "patching",
                        principalTable: "hosts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_device_investigations_HostId_OpenedUtc",
                schema: "patching",
                table: "device_investigations",
                columns: new[] { "HostId", "OpenedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_device_investigations_OpenedUtc",
                schema: "patching",
                table: "device_investigations",
                column: "OpenedUtc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "device_investigation_settings",
                schema: "patching");

            migrationBuilder.DropTable(
                name: "device_investigations",
                schema: "patching");
        }
    }
}
