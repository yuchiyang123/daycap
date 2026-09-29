using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DayCap.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class TemplatesOnboarding : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "TemplateCode",
                table: "Profiles",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TemplateSnapshot",
                table: "Profiles",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "TemplateVersion",
                table: "Profiles",
                type: "INTEGER",
                nullable: true);

            // 已經在用的人不用再走新手引導（§20.9 只給新使用者）
            migrationBuilder.Sql("UPDATE Profiles SET OnboardedAt = CreatedAt WHERE OnboardedAt IS NULL;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "TemplateCode",
                table: "Profiles");

            migrationBuilder.DropColumn(
                name: "TemplateSnapshot",
                table: "Profiles");

            migrationBuilder.DropColumn(
                name: "TemplateVersion",
                table: "Profiles");
        }
    }
}
