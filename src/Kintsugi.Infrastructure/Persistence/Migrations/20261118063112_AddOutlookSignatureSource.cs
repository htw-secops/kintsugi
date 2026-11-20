using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kintsugi.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddOutlookSignatureSource : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "outlook_signature_source_settings",
                schema: "patching",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    EndpointUrl = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    WindowsUsernameSuffix = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false, defaultValue: ""),
                    LastPullUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastPullStatus = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    LastPullCount = table.Column<int>(type: "integer", nullable: true),
                    LastPullSkipped = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    LastPullSkippedReason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    LastError = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_outlook_signature_source_settings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "person_signatures",
                schema: "patching",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Email = table.Column<string>(type: "character varying(254)", maxLength: 254, nullable: false),
                    Html = table.Column<string>(type: "text", nullable: false),
                    Text = table.Column<string>(type: "text", nullable: false),
                    Sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    RenderedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    PulledAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_person_signatures", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_person_signatures_Email",
                schema: "patching",
                table: "person_signatures",
                column: "Email",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "outlook_signature_source_settings",
                schema: "patching");

            migrationBuilder.DropTable(
                name: "person_signatures",
                schema: "patching");
        }
    }
}
