using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HnHMapperServer.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddTimerWarningsTable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TimerWarnings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TimerId = table.Column<int>(type: "INTEGER", nullable: false),
                    WarningMinutes = table.Column<int>(type: "INTEGER", nullable: false),
                    SentAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TimerWarnings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TimerWarnings_Timers_TimerId",
                        column: x => x.TimerId,
                        principalTable: "Timers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TimerWarnings_TimerId",
                table: "TimerWarnings",
                column: "TimerId");

            migrationBuilder.CreateIndex(
                name: "IX_TimerWarnings_TimerId_WarningMinutes",
                table: "TimerWarnings",
                columns: new[] { "TimerId", "WarningMinutes" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TimerWarnings");
        }
    }
}
