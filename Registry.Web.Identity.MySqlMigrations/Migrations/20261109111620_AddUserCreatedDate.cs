using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Registry.Web.Identity.MySqlMigrations.Migrations
{
    /// <summary>
    /// Adds <c>AspNetUsers.CreatedDate</c>, when the user was created (<c>User.CreatedDate</c>, UTC): nullable, and
    /// NULL for the users that exist already, whose creation date is not known. Down drops the column.
    /// </summary>
    public partial class AddUserCreatedDate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "CreatedDate",
                table: "AspNetUsers",
                type: "datetime(6)",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CreatedDate",
                table: "AspNetUsers");
        }
    }
}
