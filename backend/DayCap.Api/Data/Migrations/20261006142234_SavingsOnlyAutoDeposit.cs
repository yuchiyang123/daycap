using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DayCap.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class SavingsOnlyAutoDeposit : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "SavingsOnlyAccounts",
                table: "Profiles",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "AutoKey",
                table: "AssetAdjustments",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_AssetAdjustments_UserId_AutoKey",
                table: "AssetAdjustments",
                columns: new[] { "UserId", "AutoKey" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_AssetAdjustments_UserId_AutoKey",
                table: "AssetAdjustments");

            migrationBuilder.DropColumn(
                name: "SavingsOnlyAccounts",
                table: "Profiles");

            migrationBuilder.DropColumn(
                name: "AutoKey",
                table: "AssetAdjustments");
        }
    }
}
