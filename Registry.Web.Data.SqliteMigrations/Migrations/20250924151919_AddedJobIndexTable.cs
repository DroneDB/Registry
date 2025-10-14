using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Registry.Web.Data.SqliteMigrations.Migrations
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
                    JobId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    OrgSlug = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    DsSlug = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Path = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true),
                    UserId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    Queue = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastStateChangeUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CurrentState = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    MethodDisplay = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true),
                    ProcessingAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    SucceededAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    FailedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    DeletedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ScheduledAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JobIndices", x => x.JobId);
                });

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
