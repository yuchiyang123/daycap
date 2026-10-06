using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DayCap.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AutoTransfersDca : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AutoKey",
                table: "AccountTransfers",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "HoldingPurchases",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    UserId = table.Column<string>(type: "TEXT", nullable: false),
                    HoldingId = table.Column<int>(type: "INTEGER", nullable: false),
                    FixedItemId = table.Column<int>(type: "INTEGER", nullable: false),
                    DueDate = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    TradeDate = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    Budget = table.Column<decimal>(type: "TEXT", precision: 18, scale: 2, nullable: false),
                    Price = table.Column<decimal>(type: "TEXT", precision: 18, scale: 2, nullable: false),
                    Shares = table.Column<decimal>(type: "TEXT", precision: 18, scale: 2, nullable: false),
                    Spent = table.Column<decimal>(type: "TEXT", precision: 18, scale: 2, nullable: false),
                    FromAccountId = table.Column<int>(type: "INTEGER", nullable: false),
                    AutoKey = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ReplacesId = table.Column<int>(type: "INTEGER", nullable: true),
                    IsVoid = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HoldingPurchases", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AccountTransfers_UserId_AutoKey",
                table: "AccountTransfers",
                columns: new[] { "UserId", "AutoKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_HoldingPurchases_UserId_AutoKey",
                table: "HoldingPurchases",
                columns: new[] { "UserId", "AutoKey" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "HoldingPurchases");

            migrationBuilder.DropIndex(
                name: "IX_AccountTransfers_UserId_AutoKey",
                table: "AccountTransfers");

            migrationBuilder.DropColumn(
                name: "AutoKey",
                table: "AccountTransfers");
        }
    }
}
