using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Registry.Web.Identity.SqliteMigrations.Migrations
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
            // The scaffolded default is '', which does not read back as a dictionary: every existing user would
            // fail to load. SQLite accepts a NOT NULL column with a constant default on a table that has rows.
            migrationBuilder.AddColumn<string>(
                name: "Preferences",
                table: "AspNetUsers",
                type: "TEXT",
                nullable: false,
                defaultValue: EmptyPreferences);
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
