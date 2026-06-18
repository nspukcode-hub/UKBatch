using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace UKBatch.Storage.EntityFrameworkCore.Migrations.Postgres
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
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ScheduleStates",
                columns: table => new
                {
                    BatchDefinitionId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    LastFiredOccurrenceUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
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
