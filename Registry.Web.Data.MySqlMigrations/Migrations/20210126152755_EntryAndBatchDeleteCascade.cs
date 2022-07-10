using Microsoft.EntityFrameworkCore.Migrations;

namespace Registry.Web.Data.MySqlMigrations.Migrations
{
    public partial class EntryAndBatchDeleteCascade : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Batches_Datasets_DatasetId",
                table: "Batches");

            migrationBuilder.DropForeignKey(
                name: "FK_DownloadPackages_Datasets_DatasetId",
                table: "DownloadPackages");

            migrationBuilder.AddForeignKey(
                name: "FK_Batches_Datasets_DatasetId",
                table: "Batches",
                column: "DatasetId",
                principalTable: "Datasets",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_DownloadPackages_Datasets_DatasetId",
                table: "DownloadPackages",
                column: "DatasetId",
                principalTable: "Datasets",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Batches_Datasets_DatasetId",
                table: "Batches");

            migrationBuilder.DropForeignKey(
                name: "FK_DownloadPackages_Datasets_DatasetId",
                table: "DownloadPackages");

            migrationBuilder.AddForeignKey(
                name: "FK_Batches_Datasets_DatasetId",
                table: "Batches",
                column: "DatasetId",
                principalTable: "Datasets",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_DownloadPackages_Datasets_DatasetId",
                table: "DownloadPackages",
                column: "DatasetId",
                principalTable: "Datasets",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
