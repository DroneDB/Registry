using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Registry.Web.Data.SqliteMigrations.Migrations
{
    /// <inheritdoc />
    public partial class AddJobIndexWorkerServerId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "WorkerServerId",
                table: "JobIndices",
                type: "TEXT",
                maxLength: 128,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_JobIndex_DatasetToolState",
                table: "JobIndices",
                columns: new[] { "OrgSlug", "DsSlug", "ToolId", "CurrentState" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_JobIndex_DatasetToolState",
                table: "JobIndices");

            migrationBuilder.DropColumn(
                name: "WorkerServerId",
                table: "JobIndices");
        }
    }
}
