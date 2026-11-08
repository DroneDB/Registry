using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Registry.Web.Data.SqliteMigrations.Migrations
{
    /// <inheritdoc />
    public partial class AddTaskRecordColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CancelledBy",
                table: "JobIndices",
                type: "TEXT",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ErrorMessage",
                table: "JobIndices",
                type: "TEXT",
                maxLength: 2048,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OutputPathsJson",
                table: "JobIndices",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TargetLabel",
                table: "JobIndices",
                type: "TEXT",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "WaitingForNode",
                table: "JobIndices",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "WriteRecordJson",
                table: "JobIndices",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CancelledBy",
                table: "JobIndices");

            migrationBuilder.DropColumn(
                name: "ErrorMessage",
                table: "JobIndices");

            migrationBuilder.DropColumn(
                name: "OutputPathsJson",
                table: "JobIndices");

            migrationBuilder.DropColumn(
                name: "TargetLabel",
                table: "JobIndices");

            migrationBuilder.DropColumn(
                name: "WaitingForNode",
                table: "JobIndices");

            migrationBuilder.DropColumn(
                name: "WriteRecordJson",
                table: "JobIndices");
        }
    }
}
