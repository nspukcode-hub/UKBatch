using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace UKBatch.Storage.EntityFrameworkCore.Migrations.Sqlite
{
    /// <inheritdoc />
    public partial class AddBatchDefinitionMetadata : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Metadata",
                table: "BatchDefinitions",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Metadata",
                table: "BatchDefinitions");
        }
    }
}
