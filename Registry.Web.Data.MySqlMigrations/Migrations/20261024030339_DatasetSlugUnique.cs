using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Registry.Web.Data.MySqlMigrations.Migrations
{
    /// <summary>
    /// Makes the slug of a dataset unique within its organization in the database, not only in the application: the
    /// unique <c>(OrganizationSlug, Slug)</c> index of <c>Datasets</c>, which also replaces the foreign key's own index on
    /// <c>OrganizationSlug</c> (a prefix of it), and the end of the index on <c>Organizations.Slug</c>, which is the
    /// primary key.
    /// <para>
    /// <b>The data comes first.</b> Two requests that created the same slug together, before this index existed, left
    /// two datasets with one slug; a production database may hold such rows, and the index must not fail to build on them
    /// (this migration runs at start-up: a failure would leave the host down). Within each group of datasets of one
    /// organization that the database compares as one slug (<c>DatasetSlugRepair</c>, below, is the SQL: window
    /// <c>ROW_NUMBER</c> over the same columns the index covers, so the collation is the index's), the first dataset by
    /// <c>Id</c> keeps its slug, and every other one becomes <c>&lt;slug&gt;_&lt;Id&gt;</c> (cut so the whole fits the 128
    /// characters of the column; <c>&lt;slug&gt;_&lt;Id&gt;_&lt;Id&gt;</c> when another dataset of the organization already has
    /// that name). Nothing is deleted: a dataset's data is in the folder named by its <c>InternalRef</c>, not by its slug,
    /// so renaming moves nothing. The rows of <c>JobIndices</c>, which name datasets by slug, are not rewritten: they were
    /// as ambiguous as the lookups before this migration and stay with the dataset that keeps the slug.
    /// </para>
    /// <para>
    /// Count what would be renamed before deploying: <c>SELECT OrganizationSlug, Slug, COUNT(*) AS copies,
    /// GROUP_CONCAT(Id ORDER BY Id) AS ids FROM Datasets GROUP BY OrganizationSlug, Slug HAVING COUNT(*) &gt; 1</c>
    /// (SQLite: <c>group_concat(Id)</c>). <c>Down</c> restores the two indexes; the renamed slugs stay (they are valid).
    /// </para>
    /// </summary>
    public partial class DatasetSlugUnique : Migration
    {
        private static readonly string[] SlugColumns = ["OrganizationSlug", "Slug"];

        /// <summary>
        /// The repair on MySQL and MariaDB: one UPDATE joined to a derived table that holds the new names (a derived table
        /// is materialized before the update, and MySQL refuses to update a table it selects from otherwise). Slugs
        /// compare with the collation of the column, as the index will (production: <c>latin1_general_ci</c>).
        /// </summary>
        public const string DatasetSlugRepair = @"UPDATE Datasets d
JOIN (
    SELECT c.Id AS Id,
           CASE WHEN EXISTS (SELECT 1 FROM Datasets e
                             WHERE e.OrganizationSlug = c.OrganizationSlug
                               AND e.Slug = CONCAT(LEFT(c.Slug, 128 - CHAR_LENGTH(c.Id) - 1), '_', c.Id))
                THEN CONCAT(LEFT(c.Slug, 128 - 2 * CHAR_LENGTH(c.Id) - 2), '_', c.Id, '_', c.Id)
                ELSE CONCAT(LEFT(c.Slug, 128 - CHAR_LENGTH(c.Id) - 1), '_', c.Id)
           END AS NewSlug
    FROM (SELECT Id, OrganizationSlug, Slug,
                 ROW_NUMBER() OVER (PARTITION BY OrganizationSlug, Slug ORDER BY Id) AS CopyNo
          FROM Datasets) c
    WHERE c.CopyNo > 1
) r ON r.Id = d.Id
SET d.Slug = r.NewSlug";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(DatasetSlugRepair);

            // The new index first: the foreign key on OrganizationSlug needs an index at every moment, and the old
            // one is only dropped once this one can serve it
            migrationBuilder.CreateIndex(
                name: "IX_Datasets_OrganizationSlug_Slug",
                table: "Datasets",
                columns: SlugColumns,
                unique: true);

            migrationBuilder.DropIndex(
                name: "IX_Datasets_OrganizationSlug",
                table: "Datasets");

            migrationBuilder.DropIndex(
                name: "IX_Organizations_Slug",
                table: "Organizations");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_Organizations_Slug",
                table: "Organizations",
                column: "Slug");

            migrationBuilder.CreateIndex(
                name: "IX_Datasets_OrganizationSlug",
                table: "Datasets",
                column: "OrganizationSlug");

            migrationBuilder.DropIndex(
                name: "IX_Datasets_OrganizationSlug_Slug",
                table: "Datasets");
        }
    }
}
