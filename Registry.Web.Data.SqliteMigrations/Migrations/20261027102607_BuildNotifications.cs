using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Registry.Web.Data.SqliteMigrations.Migrations
{
    /// <inheritdoc />
    public partial class BuildNotifications : Migration
    {
        private static readonly string[] RequestOutcomeColumns = ["RequestJobId", "Outcome"];
        private static readonly string[] UserSentColumns = ["UserId", "SentAtUtc"];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "BuildNotifications",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    UserId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    OrgSlug = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    DsSlug = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Path = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: false),
                    Hash = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    Outcome = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Error = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    RequestJobId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    FinishedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    PendingKey = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    ClaimId = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    ClaimedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    SentAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BuildNotifications", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BuildNotifications_FinishedAtUtc",
                table: "BuildNotifications",
                column: "FinishedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_BuildNotifications_PendingKey",
                table: "BuildNotifications",
                column: "PendingKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BuildNotifications_RequestJobId_Outcome",
                table: "BuildNotifications",
                columns: RequestOutcomeColumns,
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BuildNotifications_UserId_SentAtUtc",
                table: "BuildNotifications",
                columns: UserSentColumns);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BuildNotifications");
        }
    }
}
