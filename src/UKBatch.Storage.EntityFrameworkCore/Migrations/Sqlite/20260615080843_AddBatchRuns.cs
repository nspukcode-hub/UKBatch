using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace UKBatch.Storage.EntityFrameworkCore.Migrations.Sqlite
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
                    BatchId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    BatchDefinitionId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    BatchName = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    TriggeredBy = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    StartedAtUtc = table.Column<string>(type: "TEXT", nullable: false),
                    CompletedAtUtc = table.Column<string>(type: "TEXT", nullable: true),
                    StepCount = table.Column<int>(type: "INTEGER", nullable: false),
                    Total = table.Column<int>(type: "INTEGER", nullable: false),
                    Succeeded = table.Column<int>(type: "INTEGER", nullable: false),
                    Failed = table.Column<int>(type: "INTEGER", nullable: false),
                    Cancelled = table.Column<int>(type: "INTEGER", nullable: false)
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
