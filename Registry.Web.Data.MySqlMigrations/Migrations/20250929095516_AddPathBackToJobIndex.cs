using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Registry.Web.Data.MySqlMigrations.Migrations
{
    /// <inheritdoc />
    public partial class AddPathBackToJobIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Path",
                table: "JobIndices",
                type: "longtext",
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Path",
                table: "JobIndices");
        }
    }
}
