using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SoftMedia.Server.Migrations
{
    /// <inheritdoc />
    public partial class UserSeriesPreference : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "UserSeriesPreferences",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SeriesId = table.Column<Guid>(type: "TEXT", nullable: false),
                    PreferredSubtitleLanguage = table.Column<string>(type: "TEXT", maxLength: 10, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserSeriesPreferences", x => new { x.UserId, x.SeriesId });
                    table.ForeignKey(
                        name: "FK_UserSeriesPreferences_MediaItems_SeriesId",
                        column: x => x.SeriesId,
                        principalTable: "MediaItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_UserSeriesPreferences_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_UserSeriesPreferences_SeriesId",
                table: "UserSeriesPreferences",
                column: "SeriesId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "UserSeriesPreferences");
        }
    }
}
