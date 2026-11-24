using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kintsugi.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPersonSignatureReply : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // True for the rows already held: they predate the switch, and false would take ours off every
            // reply default until the next hourly pull rewrote them. Deliberately not a model default: EF
            // would then send nothing for a false and the database would store true.
            migrationBuilder.AddColumn<bool>(
                name: "Reply",
                schema: "patching",
                table: "person_signatures",
                type: "boolean",
                nullable: false,
                defaultValue: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Reply",
                schema: "patching",
                table: "person_signatures");
        }
    }
}
