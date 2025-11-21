using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HnHMapperServer.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddDiscordIntegration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "DiscordNotificationsEnabled",
                table: "Tenants",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "DiscordWebhookUrl",
                table: "Tenants",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DiscordNotificationsEnabled",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "DiscordWebhookUrl",
                table: "Tenants");
        }
    }
}
