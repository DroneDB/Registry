using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Registry.Web.Data.MySqlMigrations.Migrations
{
    /// <inheritdoc />
    public partial class OptimizeEntityFrameworkIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "OwnerId",
                table: "Organizations",
                type: "varchar(255)",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "longtext",
                oldNullable: true)
                .Annotation("MySql:CharSet", "utf8mb4")
                .OldAnnotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AlterColumn<string>(
                name: "UserName",
                table: "Batches",
                type: "varchar(255)",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "longtext")
                .Annotation("MySql:CharSet", "utf8mb4")
                .OldAnnotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_OrganizationsUsers_UserId",
                table: "OrganizationsUsers",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_Organizations_OwnerId",
                table: "Organizations",
                column: "OwnerId");

            migrationBuilder.CreateIndex(
                name: "IX_Datasets_CreationDate",
                table: "Datasets",
                column: "CreationDate");

            migrationBuilder.CreateIndex(
                name: "IX_Datasets_InternalRef",
                table: "Datasets",
                column: "InternalRef");

            migrationBuilder.CreateIndex(
                name: "IX_Batches_Start",
                table: "Batches",
                column: "Start");

            migrationBuilder.CreateIndex(
                name: "IX_Batches_Status",
                table: "Batches",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_Batches_UserName_Status",
                table: "Batches",
                columns: new[] { "UserName", "Status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_OrganizationsUsers_UserId",
                table: "OrganizationsUsers");

            migrationBuilder.DropIndex(
                name: "IX_Organizations_OwnerId",
                table: "Organizations");

            migrationBuilder.DropIndex(
                name: "IX_Datasets_CreationDate",
                table: "Datasets");

            migrationBuilder.DropIndex(
                name: "IX_Datasets_InternalRef",
                table: "Datasets");

            migrationBuilder.DropIndex(
                name: "IX_Batches_Start",
                table: "Batches");

            migrationBuilder.DropIndex(
                name: "IX_Batches_Status",
                table: "Batches");

            migrationBuilder.DropIndex(
                name: "IX_Batches_UserName_Status",
                table: "Batches");

            migrationBuilder.AlterColumn<string>(
                name: "OwnerId",
                table: "Organizations",
                type: "longtext",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "varchar(255)",
                oldNullable: true)
                .Annotation("MySql:CharSet", "utf8mb4")
                .OldAnnotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AlterColumn<string>(
                name: "UserName",
                table: "Batches",
                type: "longtext",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "varchar(255)")
                .Annotation("MySql:CharSet", "utf8mb4")
                .OldAnnotation("MySql:CharSet", "utf8mb4");
        }
    }
}
