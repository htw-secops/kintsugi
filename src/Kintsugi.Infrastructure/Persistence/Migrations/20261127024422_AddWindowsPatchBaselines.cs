using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kintsugi.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddWindowsPatchBaselines : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "windows_patch_baselines",
                schema: "patching",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Build = table.Column<string>(type: "character varying(6)", maxLength: 6, nullable: false),
                    MinUbr = table.Column<int>(type: "integer", nullable: false),
                    EffectiveFromUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    GraceDays = table.Column<int>(type: "integer", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_windows_patch_baselines", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_windows_patch_baselines_Build",
                schema: "patching",
                table: "windows_patch_baselines",
                column: "Build",
                unique: true);

            // Where an install starts: November 2026's cumulative update (revision 9974, released on Patch
            // Tuesday, the 10th) is the minimum for each supported build, with 14 days of grace. Plain SQL so
            // the migration does not depend on the entity; a build already present keeps its row.
            migrationBuilder.Sql(@"
                INSERT INTO patching.windows_patch_baselines (""Id"", ""Build"", ""MinUbr"", ""EffectiveFromUtc"", ""GraceDays"", ""CreatedAtUtc"")
                SELECT gen_random_uuid(), b, 9974, TIMESTAMPTZ '2026-11-10 00:00:00+00', 14, now()
                FROM unnest(ARRAY['26100', '26200', '26300']) AS b
                WHERE NOT EXISTS (SELECT 1 FROM patching.windows_patch_baselines WHERE ""Build"" = b);
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "windows_patch_baselines",
                schema: "patching");
        }
    }
}
