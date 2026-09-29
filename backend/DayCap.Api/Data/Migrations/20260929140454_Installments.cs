using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DayCap.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class Installments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "InstallmentPrepayments",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    UserId = table.Column<string>(type: "TEXT", nullable: false),
                    InstallmentId = table.Column<int>(type: "INTEGER", nullable: false),
                    Date = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    Amount = table.Column<decimal>(type: "TEXT", precision: 18, scale: 2, nullable: false),
                    AccountId = table.Column<int>(type: "INTEGER", nullable: true),
                    Mode = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ReplacesId = table.Column<int>(type: "INTEGER", nullable: true),
                    IsVoid = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InstallmentPrepayments", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Installments",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    UserId = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    CategoryId = table.Column<int>(type: "INTEGER", nullable: false),
                    Mode = table.Column<int>(type: "INTEGER", nullable: false),
                    MonthlyAmount = table.Column<decimal>(type: "TEXT", precision: 18, scale: 2, nullable: true),
                    Periods = table.Column<int>(type: "INTEGER", nullable: false),
                    Principal = table.Column<decimal>(type: "TEXT", precision: 18, scale: 2, nullable: true),
                    AnnualRatePercent = table.Column<decimal>(type: "TEXT", precision: 18, scale: 2, nullable: true),
                    Fee = table.Column<decimal>(type: "TEXT", precision: 18, scale: 2, nullable: true),
                    FirstDueDate = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    Note = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ReplacesId = table.Column<int>(type: "INTEGER", nullable: true),
                    IsVoid = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Installments", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_InstallmentPrepayments_UserId_InstallmentId",
                table: "InstallmentPrepayments",
                columns: new[] { "UserId", "InstallmentId" });

            migrationBuilder.CreateIndex(
                name: "IX_Installments_UserId",
                table: "Installments",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "InstallmentPrepayments");

            migrationBuilder.DropTable(
                name: "Installments");
        }
    }
}
