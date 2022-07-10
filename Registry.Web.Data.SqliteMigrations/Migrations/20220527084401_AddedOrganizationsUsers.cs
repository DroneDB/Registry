using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Registry.Web.Data.SqliteMigrations.Migrations
{
    public partial class AddedOrganizationsUsers : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "OrganizationsUsers",
                columns: table => new
                {
                    UserId = table.Column<string>(type: "TEXT", nullable: false),
                    OrganizationSlug = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OrganizationsUsers", x => new { x.OrganizationSlug, x.UserId });
                    table.ForeignKey(
                        name: "FK_OrganizationsUsers_Organizations_OrganizationSlug",
                        column: x => x.OrganizationSlug,
                        principalTable: "Organizations",
                        principalColumn: "Slug",
                        onDelete: ReferentialAction.Cascade);
                });
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "OrganizationsUsers");
        }
    }
}
