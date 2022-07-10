using Microsoft.EntityFrameworkCore.Migrations;

namespace Registry.Web.Data.MySqlMigrations.Migrations
{
    public partial class FixCascadeAll : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Batches_Datasets_DatasetId",
                table: "Batches");

            migrationBuilder.DropForeignKey(
                name: "FK_DownloadPackages_Datasets_DatasetId",
                table: "DownloadPackages");

            migrationBuilder.DropForeignKey(
                name: "FK_Entries_Batches_BatchToken",
                table: "Entries");

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

            migrationBuilder.AddForeignKey(
                name: "FK_Entries_Batches_BatchToken",
                table: "Entries",
                column: "BatchToken",
                principalTable: "Batches",
                principalColumn: "Token",
                onDelete: ReferentialAction.Cascade);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Batches_Datasets_DatasetId",
                table: "Batches");

            migrationBuilder.DropForeignKey(
                name: "FK_DownloadPackages_Datasets_DatasetId",
                table: "DownloadPackages");

            migrationBuilder.DropForeignKey(
                name: "FK_Entries_Batches_BatchToken",
                table: "Entries");

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

            migrationBuilder.AddForeignKey(
                name: "FK_Entries_Batches_BatchToken",
                table: "Entries",
                column: "BatchToken",
                principalTable: "Batches",
                principalColumn: "Token",
                onDelete: ReferentialAction.Restrict);
        }
    }
}
