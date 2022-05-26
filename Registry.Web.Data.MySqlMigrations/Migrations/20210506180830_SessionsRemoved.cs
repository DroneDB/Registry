using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Registry.Web.Data.MySqlMigrations.Migrations
{
    public partial class SessionsRemoved : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Entries_Batches_BatchToken",
                table: "Entries");

            migrationBuilder.DropTable(
                name: "FileChunks");

            migrationBuilder.DropTable(
                name: "UploadSessions");

            migrationBuilder.DropPrimaryKey(
                name: "PK_Entries",
                table: "Entries");

            migrationBuilder.DropColumn(
                name: "Meta",
                table: "Datasets");

            migrationBuilder.RenameTable(
                name: "Entries",
                newName: "Entry");

            migrationBuilder.RenameIndex(
                name: "IX_Entries_BatchToken",
                table: "Entry",
                newName: "IX_Entry_BatchToken");

            migrationBuilder.AddPrimaryKey(
                name: "PK_Entry",
                table: "Entry",
                column: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_Entry_Batches_BatchToken",
                table: "Entry",
                column: "BatchToken",
                principalTable: "Batches",
                principalColumn: "Token",
                onDelete: ReferentialAction.Cascade);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Entry_Batches_BatchToken",
                table: "Entry");

            migrationBuilder.DropPrimaryKey(
                name: "PK_Entry",
                table: "Entry");

            migrationBuilder.RenameTable(
                name: "Entry",
                newName: "Entries");

            migrationBuilder.RenameIndex(
                name: "IX_Entry_BatchToken",
                table: "Entries",
                newName: "IX_Entries_BatchToken");

            migrationBuilder.AddColumn<string>(
                name: "Meta",
                table: "Datasets",
                type: "longtext CHARACTER SET utf8mb4",
                nullable: true);

            migrationBuilder.AddPrimaryKey(
                name: "PK_Entries",
                table: "Entries",
                column: "Id");

            migrationBuilder.CreateTable(
                name: "UploadSessions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    ChunksCount = table.Column<int>(type: "int", nullable: false),
                    EndedOn = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    FileName = table.Column<string>(type: "longtext CHARACTER SET utf8mb4", nullable: true),
                    StartedOn = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    TotalSize = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UploadSessions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "FileChunks",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    Date = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    Index = table.Column<int>(type: "int", nullable: false),
                    SessionId = table.Column<int>(type: "int", nullable: false),
                    Size = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FileChunks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_FileChunks_UploadSessions_SessionId",
                        column: x => x.SessionId,
                        principalTable: "UploadSessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_FileChunks_SessionId",
                table: "FileChunks",
                column: "SessionId");

            migrationBuilder.AddForeignKey(
                name: "FK_Entries_Batches_BatchToken",
                table: "Entries",
                column: "BatchToken",
                principalTable: "Batches",
                principalColumn: "Token",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
