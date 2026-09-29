using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DayCap.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class WebPush : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateOnly>(
                name: "LastPushOn",
                table: "Profiles",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "PushEnabled",
                table: "Profiles",
                type: "INTEGER",
                nullable: false,
                defaultValue: true); // 已經在用的人預設也開著（還要在裝置上允許通知才會收到）

            migrationBuilder.AddColumn<string>(
                name: "PushTime",
                table: "Profiles",
                type: "TEXT",
                nullable: false,
                defaultValue: "21:30");

            migrationBuilder.CreateTable(
                name: "AppSecrets",
                columns: table => new
                {
                    Key = table.Column<string>(type: "TEXT", nullable: false),
                    Value = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AppSecrets", x => x.Key);
                });

            migrationBuilder.CreateTable(
                name: "PushEndpoints",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    UserId = table.Column<string>(type: "TEXT", nullable: false),
                    Endpoint = table.Column<string>(type: "TEXT", nullable: false),
                    P256dh = table.Column<string>(type: "TEXT", nullable: false),
                    Auth = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastSuccessAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    FailCount = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PushEndpoints", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PushEndpoints_Endpoint",
                table: "PushEndpoints",
                column: "Endpoint",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PushEndpoints_UserId",
                table: "PushEndpoints",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AppSecrets");

            migrationBuilder.DropTable(
                name: "PushEndpoints");

            migrationBuilder.DropColumn(
                name: "LastPushOn",
                table: "Profiles");

            migrationBuilder.DropColumn(
                name: "PushEnabled",
                table: "Profiles");

            migrationBuilder.DropColumn(
                name: "PushTime",
                table: "Profiles");
        }
    }
}
