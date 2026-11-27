using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kintsugi.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddHostWindowsUpdateState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "WindowsUpdateStateJson",
                schema: "patching",
                table: "hosts",
                type: "character varying(4000)",
                maxLength: 4000,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "WindowsUpdateStateJson",
                schema: "patching",
                table: "hosts");
        }
    }
}
