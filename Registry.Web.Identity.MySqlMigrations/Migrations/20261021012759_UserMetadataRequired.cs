using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Registry.Web.Identity.MySqlMigrations.Migrations
{
    /// <summary>
    /// Brings the MySQL Identity schema to the current model (the only migration before this one is
    /// the 2021 InitialCreate): <c>AspNetUsers.Metadata</c> becomes NOT NULL, as it is in the model
    /// and in the SQLite schema.
    /// <para>
    /// The model snapshot also gains the IdentityColumn annotation of <c>AspNetUserClaims.Id</c> and
    /// <c>AspNetRoleClaims.Id</c>. That is a snapshot-only correction: InitialCreate already created
    /// both columns AUTO_INCREMENT (only its snapshot lacked the annotation), so the scaffolded
    /// AlterColumn operations for them, which would rebuild both tables for no change, and their
    /// Down counterparts, which would drop AUTO_INCREMENT, are left out.
    /// </para>
    /// </summary>
    public partial class UserMetadataRequired : Migration
    {
        /// <summary>
        /// What the application stores for empty metadata: <c>User.Metadata</c> defaults to an empty
        /// dictionary and is written as <c>JsonConvert.SerializeObject</c> of it (ApplicationDbContext).
        /// The scaffolded '' would read back as a null dictionary, so existing '' rows are repaired too.
        /// </summary>
        public const string EmptyMetadata = "{}";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Data first: a NULL row would make the NOT NULL change fail (or, outside strict mode,
            // silently become ''), and a '' row reads back as a null dictionary just like NULL does
            // (MariaDB's PAD SPACE collation also matches blank-only values, which read back the same)
            foreach (var invalid in new[] { null, "" })
                migrationBuilder.UpdateData(
                    table: "AspNetUsers",
                    keyColumn: "Metadata",
                    keyValue: invalid,
                    column: "Metadata",
                    value: EmptyMetadata);

            migrationBuilder.AlterColumn<string>(
                name: "Metadata",
                table: "AspNetUsers",
                type: "longtext",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "longtext CHARACTER SET utf8mb4",
                oldNullable: true)
                .Annotation("MySql:CharSet", "utf8mb4")
                .OldAnnotation("MySql:CharSet", "utf8mb4");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The mirror of Up. The scaffolder also listed every other longtext column, only to
            // restore the 2021 "longtext CHARACTER SET utf8mb4" spelling of the type they already have.
            // The repaired '{}' values stay: they are valid metadata for the nullable column too.
            migrationBuilder.AlterColumn<string>(
                name: "Metadata",
                table: "AspNetUsers",
                type: "longtext CHARACTER SET utf8mb4",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "longtext")
                .Annotation("MySql:CharSet", "utf8mb4")
                .OldAnnotation("MySql:CharSet", "utf8mb4");
        }
    }
}
