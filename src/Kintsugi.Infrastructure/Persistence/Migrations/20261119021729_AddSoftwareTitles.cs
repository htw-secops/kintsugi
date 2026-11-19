using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kintsugi.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSoftwareTitles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "script_secrets",
                schema: "patching",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Value = table.Column<string>(type: "character varying(4096)", maxLength: 4096, nullable: false),
                    ValueSignature = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    UpdatedBy = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    UpdatedUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_script_secrets", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "software_titles",
                schema: "patching",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    Platform = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    PackageManager = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    OperatingSystem = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    PackageId = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    DetectAs = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    Mode = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    SecretNames = table.Column<List<string>>(type: "text[]", nullable: false),
                    AssignedToAllHosts = table.Column<bool>(type: "boolean", nullable: false),
                    AssignedHostIds = table.Column<List<Guid>>(type: "uuid[]", nullable: false),
                    AssignedBy = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    CreatedBy = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    ActionSignature = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_software_titles", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_script_secrets_Name",
                schema: "patching",
                table: "script_secrets",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_software_titles_Name_Platform",
                schema: "patching",
                table: "software_titles",
                columns: new[] { "Name", "Platform" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "script_secrets",
                schema: "patching");

            migrationBuilder.DropTable(
                name: "software_titles",
                schema: "patching");
        }
    }
}
