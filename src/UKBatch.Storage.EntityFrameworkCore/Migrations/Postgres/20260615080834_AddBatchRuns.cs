using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace UKBatch.Storage.EntityFrameworkCore.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class AddBatchRuns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "BatchRuns",
                columns: table => new
                {
                    BatchId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    BatchDefinitionId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    BatchName = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    TriggeredBy = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    StartedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CompletedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    StepCount = table.Column<int>(type: "integer", nullable: false),
                    Total = table.Column<int>(type: "integer", nullable: false),
                    Succeeded = table.Column<int>(type: "integer", nullable: false),
                    Failed = table.Column<int>(type: "integer", nullable: false),
                    Cancelled = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BatchRuns", x => x.BatchId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BatchRuns_BatchDefinitionId_StartedAtUtc",
                table: "BatchRuns",
                columns: new[] { "BatchDefinitionId", "StartedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_BatchRuns_Status_StartedAtUtc",
                table: "BatchRuns",
                columns: new[] { "Status", "StartedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BatchRuns");
        }
    }
}
