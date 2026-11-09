using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Registry.Web.Data.SqliteMigrations.Migrations
{
    /// <inheritdoc />
    public partial class BatchCreatedDataset : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "CreatedDataset",
                table: "Batches",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            // Null for a batch from before: its start and its entries count (D-049)
            migrationBuilder.AddColumn<DateTime>(
                name: "LastActivity",
                table: "Batches",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "Replaced",
                table: "Entry",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CreatedDataset",
                table: "Batches");

            migrationBuilder.DropColumn(
                name: "LastActivity",
                table: "Batches");

            migrationBuilder.DropColumn(
                name: "Replaced",
                table: "Entry");
        }
    }
}
