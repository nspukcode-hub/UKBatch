using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace UKBatch.Storage.EntityFrameworkCore.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ApprovalGates",
                columns: table => new
                {
                    ApprovalId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    BatchId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    BatchStepId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    BatchDefinitionId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Config = table.Column<string>(type: "jsonb", nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    PendingSinceUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    DeadlineUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Outcome = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    DecidedBy = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    DecidedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Note = table.Column<string>(type: "character varying(4096)", maxLength: 4096, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApprovalGates", x => x.ApprovalId);
                });

            migrationBuilder.CreateTable(
                name: "BatchDefinitions",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Name = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    Source = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Schedule = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    Steps = table.Column<string>(type: "jsonb", nullable: false),
                    FailurePolicy = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    OnFailureSteps = table.Column<string>(type: "jsonb", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    Version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BatchDefinitions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "JobExecutions",
                columns: table => new
                {
                    ExecutionId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    JobName = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    BatchId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    BatchStepId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    BatchDefinitionId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Parameters = table.Column<string>(type: "jsonb", nullable: false),
                    EnqueuedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    StartedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CompletedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    AttemptNumber = table.Column<int>(type: "integer", nullable: false),
                    MaxRetries = table.Column<int>(type: "integer", nullable: false),
                    LastError = table.Column<string>(type: "text", nullable: true),
                    Processed = table.Column<long>(type: "bigint", nullable: false),
                    Failed = table.Column<long>(type: "bigint", nullable: false),
                    Total = table.Column<long>(type: "bigint", nullable: true),
                    TriggeredBy = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    WorkerName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JobExecutions", x => x.ExecutionId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ApprovalGates_Status",
                table: "ApprovalGates",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_BatchDefinitions_Source_CreatedAtUtc",
                table: "BatchDefinitions",
                columns: new[] { "Source", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_BatchDefinitions_Source_Name",
                table: "BatchDefinitions",
                columns: new[] { "Source", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_JobExecutions_BatchDefinitionId_EnqueuedAtUtc",
                table: "JobExecutions",
                columns: new[] { "BatchDefinitionId", "EnqueuedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_JobExecutions_BatchId",
                table: "JobExecutions",
                column: "BatchId");

            migrationBuilder.CreateIndex(
                name: "IX_JobExecutions_JobName_EnqueuedAtUtc",
                table: "JobExecutions",
                columns: new[] { "JobName", "EnqueuedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_JobExecutions_Status_EnqueuedAtUtc",
                table: "JobExecutions",
                columns: new[] { "Status", "EnqueuedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ApprovalGates");

            migrationBuilder.DropTable(
                name: "BatchDefinitions");

            migrationBuilder.DropTable(
                name: "JobExecutions");
        }
    }
}
