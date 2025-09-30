using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Registry.Web.Data.MySqlMigrations.Migrations
{
    /// <inheritdoc />
    public partial class JobIndexFromPathToHash : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "Path",
                table: "JobIndices",
                newName: "Hash");

            migrationBuilder.RenameIndex(
                name: "IX_JobIndices_OrgSlug_DsSlug_Path",
                table: "JobIndices",
                newName: "IX_JobIndices_OrgSlug_DsSlug_Hash");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "Hash",
                table: "JobIndices",
                newName: "Path");

            migrationBuilder.RenameIndex(
                name: "IX_JobIndices_OrgSlug_DsSlug_Hash",
                table: "JobIndices",
                newName: "IX_JobIndices_OrgSlug_DsSlug_Path");
        }
    }
}
