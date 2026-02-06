using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Registry.Web.Data.SqliteMigrations.Migrations
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
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "GrantedBy",
                table: "OrganizationsUsers",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Permissions",
                table: "OrganizationsUsers",
                type: "INTEGER",
                nullable: false,
                defaultValue: 1);
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
        }
    }
}
