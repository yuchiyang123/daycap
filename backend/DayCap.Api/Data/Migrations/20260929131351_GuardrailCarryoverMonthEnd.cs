using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DayCap.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class GuardrailCarryoverMonthEnd : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "OnboardedAt",
                table: "Profiles",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SeenTips",
                table: "Profiles",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SourceEntryId",
                table: "PoolTransfers",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ClosedAt",
                table: "Periods",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SourceEntryId",
                table: "AssetAdjustments",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "MonthEnds",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    UserId = table.Column<string>(type: "TEXT", nullable: false),
                    PeriodId = table.Column<int>(type: "INTEGER", nullable: false),
                    ReconciliationId = table.Column<int>(type: "INTEGER", nullable: true),
                    Result = table.Column<decimal>(type: "TEXT", precision: 18, scale: 2, nullable: false),
                    Decision = table.Column<int>(type: "INTEGER", nullable: false),
                    CarryAmount = table.Column<decimal>(type: "TEXT", precision: 18, scale: 2, nullable: false),
                    Snapshot = table.Column<string>(type: "TEXT", nullable: false),
                    ClosedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MonthEnds", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PeriodCarryovers",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    UserId = table.Column<string>(type: "TEXT", nullable: false),
                    SourcePeriodId = table.Column<int>(type: "INTEGER", nullable: false),
                    TargetDate = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    Amount = table.Column<decimal>(type: "TEXT", precision: 18, scale: 2, nullable: false),
                    Label = table.Column<string>(type: "TEXT", maxLength: 120, nullable: false),
                    SourceEntryId = table.Column<int>(type: "INTEGER", nullable: true),
                    MonthEndId = table.Column<int>(type: "INTEGER", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ReplacesId = table.Column<int>(type: "INTEGER", nullable: true),
                    IsVoid = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PeriodCarryovers", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MonthEnds_UserId_PeriodId",
                table: "MonthEnds",
                columns: new[] { "UserId", "PeriodId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PeriodCarryovers_UserId_TargetDate",
                table: "PeriodCarryovers",
                columns: new[] { "UserId", "TargetDate" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MonthEnds");

            migrationBuilder.DropTable(
                name: "PeriodCarryovers");

            migrationBuilder.DropColumn(
                name: "OnboardedAt",
                table: "Profiles");

            migrationBuilder.DropColumn(
                name: "SeenTips",
                table: "Profiles");

            migrationBuilder.DropColumn(
                name: "SourceEntryId",
                table: "PoolTransfers");

            migrationBuilder.DropColumn(
                name: "ClosedAt",
                table: "Periods");

            migrationBuilder.DropColumn(
                name: "SourceEntryId",
                table: "AssetAdjustments");
        }
    }
}
