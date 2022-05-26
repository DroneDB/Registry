using Microsoft.EntityFrameworkCore.Migrations;

namespace Registry.Web.Data.MySqlMigrations.Migrations
{
    public partial class SizeAndObjectsCountRemoved : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ObjectsCount",
                table: "Datasets");

            migrationBuilder.DropColumn(
                name: "Size",
                table: "Datasets");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ObjectsCount",
                table: "Datasets",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<long>(
                name: "Size",
                table: "Datasets",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);
        }
    }
}
