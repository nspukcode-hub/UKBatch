using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace UKBatch.Storage.EntityFrameworkCore.Migrations.Sqlite
{
    /// <inheritdoc />
    public partial class AddBatchRunCompensationCursor : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "CompensationStepIndex",
                table: "BatchRuns",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RetryOfBatchId",
                table: "BatchRuns",
                type: "TEXT",
                maxLength: 64,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CompensationStepIndex",
                table: "BatchRuns");

            migrationBuilder.DropColumn(
                name: "RetryOfBatchId",
                table: "BatchRuns");
        }
    }
}
