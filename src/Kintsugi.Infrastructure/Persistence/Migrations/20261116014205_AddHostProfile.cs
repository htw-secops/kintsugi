using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kintsugi.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddHostProfile : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AssignedEmail",
                schema: "patching",
                table: "hosts",
                type: "character varying(254)",
                maxLength: 254,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AssignedName",
                schema: "patching",
                table: "hosts",
                type: "character varying(150)",
                maxLength: 150,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Department",
                schema: "patching",
                table: "hosts",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Location",
                schema: "patching",
                table: "hosts",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Model",
                schema: "patching",
                table: "hosts",
                type: "character varying(150)",
                maxLength: 150,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Organization",
                schema: "patching",
                table: "hosts",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AssignedEmail",
                schema: "patching",
                table: "hosts");

            migrationBuilder.DropColumn(
                name: "AssignedName",
                schema: "patching",
                table: "hosts");

            migrationBuilder.DropColumn(
                name: "Department",
                schema: "patching",
                table: "hosts");

            migrationBuilder.DropColumn(
                name: "Location",
                schema: "patching",
                table: "hosts");

            migrationBuilder.DropColumn(
                name: "Model",
                schema: "patching",
                table: "hosts");

            migrationBuilder.DropColumn(
                name: "Organization",
                schema: "patching",
                table: "hosts");
        }
    }
}
