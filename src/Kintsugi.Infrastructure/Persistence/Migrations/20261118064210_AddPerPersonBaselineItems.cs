using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kintsugi.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPerPersonBaselineItems : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_host_baseline_item_states_HostId_BaselineVersionId_ItemKey",
                schema: "patching",
                table: "host_baseline_item_states");

            migrationBuilder.AddColumn<string>(
                name: "Subject",
                schema: "patching",
                table: "host_baseline_item_states",
                type: "character varying(254)",
                maxLength: 254,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "AssignedApplicationName",
                schema: "patching",
                table: "baselines",
                type: "character varying(255)",
                maxLength: 255,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "host_person_resolutions",
                schema: "patching",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    HostId = table.Column<Guid>(type: "uuid", nullable: false),
                    WindowsUser = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Email = table.Column<string>(type: "character varying(254)", maxLength: 254, nullable: true),
                    ResolvedBy = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    Reason = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    LastSeenUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_host_person_resolutions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_host_person_resolutions_hosts_HostId",
                        column: x => x.HostId,
                        principalSchema: "patching",
                        principalTable: "hosts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_host_baseline_item_states_HostId_BaselineVersionId_ItemKey_~",
                schema: "patching",
                table: "host_baseline_item_states",
                columns: new[] { "HostId", "BaselineVersionId", "ItemKey", "Subject" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_host_person_resolutions_HostId_WindowsUser",
                schema: "patching",
                table: "host_person_resolutions",
                columns: new[] { "HostId", "WindowsUser" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_host_person_resolutions_LastSeenUtc",
                schema: "patching",
                table: "host_person_resolutions",
                column: "LastSeenUtc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "host_person_resolutions",
                schema: "patching");

            migrationBuilder.DropIndex(
                name: "IX_host_baseline_item_states_HostId_BaselineVersionId_ItemKey_~",
                schema: "patching",
                table: "host_baseline_item_states");

            migrationBuilder.DropColumn(
                name: "Subject",
                schema: "patching",
                table: "host_baseline_item_states");

            migrationBuilder.DropColumn(
                name: "AssignedApplicationName",
                schema: "patching",
                table: "baselines");

            migrationBuilder.CreateIndex(
                name: "IX_host_baseline_item_states_HostId_BaselineVersionId_ItemKey",
                schema: "patching",
                table: "host_baseline_item_states",
                columns: new[] { "HostId", "BaselineVersionId", "ItemKey" },
                unique: true);
        }
    }
}
