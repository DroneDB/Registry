using Microsoft.EntityFrameworkCore.Migrations;

namespace Registry.Web.Data.MySqlMigrations.Migrations
{
    public partial class BatchDeleteCascade : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Entries_Batches_BatchToken",
                table: "Entries");

            migrationBuilder.AddForeignKey(
                name: "FK_Entries_Batches_BatchToken",
                table: "Entries",
                column: "BatchToken",
                principalTable: "Batches",
                principalColumn: "Token",
                onDelete: ReferentialAction.Restrict);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Entries_Batches_BatchToken",
                table: "Entries");

            migrationBuilder.AddForeignKey(
                name: "FK_Entries_Batches_BatchToken",
                table: "Entries",
                column: "BatchToken",
                principalTable: "Batches",
                principalColumn: "Token",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
