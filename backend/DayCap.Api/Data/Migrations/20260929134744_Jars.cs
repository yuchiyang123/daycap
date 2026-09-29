using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DayCap.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class Jars : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "JarId",
                table: "PoolTransfers",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "SpreadShortfall",
                table: "PoolTransfers",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<decimal>(
                name: "JarCovered",
                table: "Entries",
                type: "TEXT",
                precision: 18,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<int>(
                name: "JarId",
                table: "Entries",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Jars",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    UserId = table.Column<string>(type: "TEXT", nullable: false),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    TargetAmount = table.Column<decimal>(type: "TEXT", precision: 18, scale: 2, nullable: false),
                    DueDate = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    MonthlyAmount = table.Column<decimal>(type: "TEXT", precision: 18, scale: 2, nullable: true),
                    AutoSurplusPercent = table.Column<decimal>(type: "TEXT", precision: 18, scale: 2, nullable: true),
                    FixedItemId = table.Column<int>(type: "INTEGER", nullable: true),
                    ClosedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ClosedByEntryId = table.Column<int>(type: "INTEGER", nullable: true),
                    SortOrder = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Jars", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Jars_UserId",
                table: "Jars",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Jars");

            migrationBuilder.DropColumn(
                name: "JarId",
                table: "PoolTransfers");

            migrationBuilder.DropColumn(
                name: "SpreadShortfall",
                table: "PoolTransfers");

            migrationBuilder.DropColumn(
                name: "JarCovered",
                table: "Entries");

            migrationBuilder.DropColumn(
                name: "JarId",
                table: "Entries");
        }
    }
}
