using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Registry.Web.Data.MySqlMigrations.Migrations
{
    /// <inheritdoc />
    public partial class AddedJobIndexTable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "JobIndices",
                columns: table => new
                {
                    JobId = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    OrgSlug = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    DsSlug = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Path = table.Column<string>(type: "varchar(1024)", maxLength: 1024, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    UserId = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Queue = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    LastStateChangeUtc = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    CurrentState = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    MethodDisplay = table.Column<string>(type: "varchar(1024)", maxLength: 1024, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    ProcessingAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    SucceededAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    FailedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    DeletedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    ScheduledAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JobIndices", x => x.JobId);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_JobIndices_CreatedAtUtc",
                table: "JobIndices",
                column: "CreatedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_JobIndices_OrgSlug_DsSlug",
                table: "JobIndices",
                columns: new[] { "OrgSlug", "DsSlug" });

            migrationBuilder.CreateIndex(
                name: "IX_JobIndices_OrgSlug_DsSlug_Path",
                table: "JobIndices",
                columns: new[] { "OrgSlug", "DsSlug", "Path" });

            migrationBuilder.CreateIndex(
                name: "IX_JobIndices_UserId",
                table: "JobIndices",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "JobIndices");
        }
    }
}
