using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Registry.Web.Identity.MySqlMigrations.Migrations
{
    /// <summary>
    /// Adds <c>AspNetUsers.Preferences</c>, the user's Hub preferences (<c>User.Preferences</c>): NOT NULL, and
    /// <see cref="EmptyPreferences"/> for the users that exist already.
    /// </summary>
    public partial class AddUserPreferences : Migration
    {
        /// <summary>What the application stores for no preferences: the serialized empty dictionary.</summary>
        public const string EmptyPreferences = "{}";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // MySQL refuses a literal default on a longtext column, so the column is added nullable, filled for the
            // users that exist, and only then made NOT NULL (a NULL row would make that change fail, or, outside
            // strict mode, silently become '' which does not read back as a dictionary)
            migrationBuilder.AddColumn<string>(
                name: "Preferences",
                table: "AspNetUsers",
                type: "longtext",
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.Sql($"UPDATE `AspNetUsers` SET `Preferences` = '{EmptyPreferences}'");

            migrationBuilder.AlterColumn<string>(
                name: "Preferences",
                table: "AspNetUsers",
                type: "longtext",
                nullable: false,
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
                name: "Preferences",
                table: "AspNetUsers");
        }
    }
}
