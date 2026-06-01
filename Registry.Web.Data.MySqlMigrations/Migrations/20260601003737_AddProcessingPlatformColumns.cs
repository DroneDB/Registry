using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Registry.Web.Data.MySqlMigrations.Migrations
{
    /// <inheritdoc />
    public partial class AddProcessingPlatformColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ArtifactSha256",
                table: "JobIndices",
                type: "varchar(64)",
                maxLength: 64,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<long>(
                name: "ArtifactSizeBytes",
                table: "JobIndices",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ErrorType",
                table: "JobIndices",
                type: "varchar(128)",
                maxLength: 128,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<string>(
                name: "LogTailJson",
                table: "JobIndices",
                type: "longtext",
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<string>(
                name: "ParentJobId",
                table: "JobIndices",
                type: "varchar(36)",
                maxLength: 36,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<string>(
                name: "PhaseMessage",
                table: "JobIndices",
                type: "varchar(256)",
                maxLength: 256,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<int>(
                name: "ProgressPercent",
                table: "JobIndices",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ProgressUpdatedAtUtc",
                table: "JobIndices",
                type: "datetime(6)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RequestHash",
                table: "JobIndices",
                type: "varchar(64)",
                maxLength: 64,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<string>(
                name: "ToolId",
                table: "JobIndices",
                type: "varchar(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "build")
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<string>(
                name: "ToolVersion",
                table: "JobIndices",
                type: "varchar(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "1")
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<string>(
                name: "WorkflowExecutionId",
                table: "JobIndices",
                type: "varchar(36)",
                maxLength: 36,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_JobIndex_Parent",
                table: "JobIndices",
                column: "ParentJobId");

            migrationBuilder.CreateIndex(
                name: "IX_JobIndex_RequestHash",
                table: "JobIndices",
                columns: new[] { "OrgSlug", "DsSlug", "ToolId", "RequestHash" });

            migrationBuilder.CreateIndex(
                name: "IX_JobIndex_Tool_State",
                table: "JobIndices",
                columns: new[] { "Queue", "OrgSlug", "DsSlug", "ToolId", "CurrentState", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_JobIndex_Workflow",
                table: "JobIndices",
                column: "WorkflowExecutionId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_JobIndex_Parent",
                table: "JobIndices");

            migrationBuilder.DropIndex(
                name: "IX_JobIndex_RequestHash",
                table: "JobIndices");

            migrationBuilder.DropIndex(
                name: "IX_JobIndex_Tool_State",
                table: "JobIndices");

            migrationBuilder.DropIndex(
                name: "IX_JobIndex_Workflow",
                table: "JobIndices");

            migrationBuilder.DropColumn(
                name: "ArtifactSha256",
                table: "JobIndices");

            migrationBuilder.DropColumn(
                name: "ArtifactSizeBytes",
                table: "JobIndices");

            migrationBuilder.DropColumn(
                name: "ErrorType",
                table: "JobIndices");

            migrationBuilder.DropColumn(
                name: "LogTailJson",
                table: "JobIndices");

            migrationBuilder.DropColumn(
                name: "ParentJobId",
                table: "JobIndices");

            migrationBuilder.DropColumn(
                name: "PhaseMessage",
                table: "JobIndices");

            migrationBuilder.DropColumn(
                name: "ProgressPercent",
                table: "JobIndices");

            migrationBuilder.DropColumn(
                name: "ProgressUpdatedAtUtc",
                table: "JobIndices");

            migrationBuilder.DropColumn(
                name: "RequestHash",
                table: "JobIndices");

            migrationBuilder.DropColumn(
                name: "ToolId",
                table: "JobIndices");

            migrationBuilder.DropColumn(
                name: "ToolVersion",
                table: "JobIndices");

            migrationBuilder.DropColumn(
                name: "WorkflowExecutionId",
                table: "JobIndices");
        }
    }
}
