using Microsoft.EntityFrameworkCore.Migrations;

namespace Registry.Web.Data.MySqlMigrations.Migrations
{
    public partial class AddFileTypesAndRemoveNameAndRemoveDownloadPackages : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DownloadPackages");

            migrationBuilder.DropColumn(
                name: "Name",
                table: "Datasets");

            migrationBuilder.AddColumn<string>(
                name: "FileTypes",
                table: "Datasets",
                type: "longtext",
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "FileTypes",
                table: "Datasets");

            migrationBuilder.AddColumn<string>(
                name: "Name",
                table: "Datasets",
                type: "longtext",
                nullable: false)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "DownloadPackages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    CreationDate = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    DatasetId = table.Column<int>(type: "int", nullable: false),
                    ExpirationDate = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    IsPublic = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    Paths = table.Column<string>(type: "longtext", nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    UserName = table.Column<string>(type: "longtext", nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DownloadPackages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DownloadPackages_Datasets_DatasetId",
                        column: x => x.DatasetId,
                        principalTable: "Datasets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_DownloadPackages_DatasetId",
                table: "DownloadPackages",
                column: "DatasetId");
        }
    }
}
