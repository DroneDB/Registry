using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Registry.Web.Identity.SqliteMigrations.Migrations
{
    /// <summary>
    /// Data repair, no schema change. SQLite identity databases created before this assembly existed
    /// (2020-07 to 2022-05, by <c>EnsureCreated</c> with nullable reference types off) have a nullable
    /// <c>AspNetUsers.Metadata</c>; <c>SafeMigrateAsync</c> stamps them at InitialCreate without
    /// touching the column, so they can hold NULL. NULL and '' both read back as a null dictionary,
    /// which <c>UsersManager</c> dereferences. Both become <see cref="EmptyMetadata"/>, what the
    /// application writes for empty metadata (the serialized empty dictionary <c>User.Metadata</c>
    /// starts as). The MySQL counterpart is part of UserMetadataRequired.
    /// </summary>
    public partial class RepairUserMetadata : Migration
    {
        /// <summary>What the application stores for empty metadata.</summary>
        public const string EmptyMetadata = "{}";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            foreach (var invalid in new[] { null, "" })
                migrationBuilder.UpdateData(
                    table: "AspNetUsers",
                    keyColumn: "Metadata",
                    keyValue: invalid,
                    column: "Metadata",
                    value: EmptyMetadata);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Nothing to undo: '{}' is valid metadata for every schema version
        }
    }
}
