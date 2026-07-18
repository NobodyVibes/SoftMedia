using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SoftMedia.Server.Migrations
{
    /// <inheritdoc />
    public partial class AddPlaybackHistory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PlaybackHistory",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    UserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    MediaItemId = table.Column<Guid>(type: "TEXT", nullable: false),
                    MediaType = table.Column<int>(type: "INTEGER", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastBeatAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    MaxPosition = table.Column<double>(type: "REAL", nullable: false),
                    Completed = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlaybackHistory", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PlaybackHistory_MediaItems_MediaItemId",
                        column: x => x.MediaItemId,
                        principalTable: "MediaItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PlaybackHistory_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PlaybackHistory_MediaItemId",
                table: "PlaybackHistory",
                column: "MediaItemId");

            migrationBuilder.CreateIndex(
                name: "IX_PlaybackHistory_UserId_LastBeatAt",
                table: "PlaybackHistory",
                columns: new[] { "UserId", "LastBeatAt" });

            migrationBuilder.CreateIndex(
                name: "IX_PlaybackHistory_UserId_MediaItemId",
                table: "PlaybackHistory",
                columns: new[] { "UserId", "MediaItemId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PlaybackHistory");
        }
    }
}
