using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Registry.Web.Data.SqliteMigrations.Migrations
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
        /// The repair on SQLite: the new names are computed first into a temporary table (an UPDATE that reads the table it
        /// writes would see its own renames), then applied. Slugs compare as bytes here, as the index will.
        /// </summary>
        public static readonly string[] DatasetSlugRepair =
        [
            @"CREATE TEMP TABLE DatasetSlugRepair AS
SELECT c.Id AS Id,
       CASE WHEN EXISTS (SELECT 1 FROM Datasets e
                         WHERE e.OrganizationSlug = c.OrganizationSlug
                           AND e.Slug = SUBSTR(c.Slug, 1, 128 - LENGTH(c.Id) - 1) || '_' || c.Id)
            THEN SUBSTR(c.Slug, 1, 128 - 2 * LENGTH(c.Id) - 2) || '_' || c.Id || '_' || c.Id
            ELSE SUBSTR(c.Slug, 1, 128 - LENGTH(c.Id) - 1) || '_' || c.Id
       END AS NewSlug
FROM (SELECT Id, OrganizationSlug, Slug,
             ROW_NUMBER() OVER (PARTITION BY OrganizationSlug, Slug ORDER BY Id) AS CopyNo
      FROM Datasets) c
WHERE c.CopyNo > 1",
            @"UPDATE Datasets
SET Slug = (SELECT r.NewSlug FROM DatasetSlugRepair r WHERE r.Id = Datasets.Id)
WHERE Id IN (SELECT Id FROM DatasetSlugRepair)",
            "DROP TABLE DatasetSlugRepair"
        ];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            foreach (var statement in DatasetSlugRepair)
                migrationBuilder.Sql(statement);

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
