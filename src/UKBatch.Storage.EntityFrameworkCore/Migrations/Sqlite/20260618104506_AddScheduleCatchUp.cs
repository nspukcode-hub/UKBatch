using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace UKBatch.Storage.EntityFrameworkCore.Migrations.Sqlite
{
    /// <inheritdoc />
    public partial class AddScheduleCatchUp : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "ScheduleCatchUpWindowTicks",
                table: "BatchDefinitions",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ScheduleStates",
                columns: table => new
                {
                    BatchDefinitionId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    LastFiredOccurrenceUtc = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScheduleStates", x => x.BatchDefinitionId);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ScheduleStates");

            migrationBuilder.DropColumn(
                name: "ScheduleCatchUpWindowTicks",
                table: "BatchDefinitions");
        }
    }
}
