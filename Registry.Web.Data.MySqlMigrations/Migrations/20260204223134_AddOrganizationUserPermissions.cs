using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Registry.Web.Data.MySqlMigrations.Migrations
{
    /// <inheritdoc />
    public partial class AddOrganizationUserPermissions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "GrantedAt",
                table: "OrganizationsUsers",
                type: "datetime(6)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "GrantedBy",
                table: "OrganizationsUsers",
                type: "longtext",
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<int>(
                name: "Permissions",
                table: "OrganizationsUsers",
                type: "int",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AlterColumn<string>(
                name: "Path",
                table: "JobIndices",
                type: "varchar(2048)",
                maxLength: 2048,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "longtext",
                oldNullable: true)
                .Annotation("MySql:CharSet", "utf8mb4")
                .OldAnnotation("MySql:CharSet", "utf8mb4");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "GrantedAt",
                table: "OrganizationsUsers");

            migrationBuilder.DropColumn(
                name: "GrantedBy",
                table: "OrganizationsUsers");

            migrationBuilder.DropColumn(
                name: "Permissions",
                table: "OrganizationsUsers");

            migrationBuilder.AlterColumn<string>(
                name: "Path",
                table: "JobIndices",
                type: "longtext",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "varchar(2048)",
                oldMaxLength: 2048,
                oldNullable: true)
                .Annotation("MySql:CharSet", "utf8mb4")
                .OldAnnotation("MySql:CharSet", "utf8mb4");
        }
    }
}
