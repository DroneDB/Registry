using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Registry.Web.Data.MySqlMigrations.Migrations
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
                type: "varchar(128)",
                maxLength: 128,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<string>(
                name: "ErrorMessage",
                table: "JobIndices",
                type: "varchar(2048)",
                maxLength: 2048,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<string>(
                name: "OutputPathsJson",
                table: "JobIndices",
                type: "longtext",
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<string>(
                name: "TargetLabel",
                table: "JobIndices",
                type: "varchar(256)",
                maxLength: 256,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<bool>(
                name: "WaitingForNode",
                table: "JobIndices",
                type: "tinyint(1)",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "WriteRecordJson",
                table: "JobIndices",
                type: "longtext",
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");
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
