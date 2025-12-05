using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SoftMedia.Server.Migrations
{
    /// <inheritdoc />
    public partial class AddMusicHierarchy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "AlbumId",
                table: "MediaItems",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ArtistId",
                table: "MediaItems",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "DiscNumber",
                table: "MediaItems",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "TrackNumber",
                table: "MediaItems",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_MediaItems_AlbumId",
                table: "MediaItems",
                column: "AlbumId");

            migrationBuilder.CreateIndex(
                name: "IX_MediaItems_ArtistId",
                table: "MediaItems",
                column: "ArtistId");

            migrationBuilder.AddForeignKey(
                name: "FK_MediaItems_MediaItems_AlbumId",
                table: "MediaItems",
                column: "AlbumId",
                principalTable: "MediaItems",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_MediaItems_MediaItems_ArtistId",
                table: "MediaItems",
                column: "ArtistId",
                principalTable: "MediaItems",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_MediaItems_MediaItems_AlbumId",
                table: "MediaItems");

            migrationBuilder.DropForeignKey(
                name: "FK_MediaItems_MediaItems_ArtistId",
                table: "MediaItems");

            migrationBuilder.DropIndex(
                name: "IX_MediaItems_AlbumId",
                table: "MediaItems");

            migrationBuilder.DropIndex(
                name: "IX_MediaItems_ArtistId",
                table: "MediaItems");

            migrationBuilder.DropColumn(
                name: "AlbumId",
                table: "MediaItems");

            migrationBuilder.DropColumn(
                name: "ArtistId",
                table: "MediaItems");

            migrationBuilder.DropColumn(
                name: "DiscNumber",
                table: "MediaItems");

            migrationBuilder.DropColumn(
                name: "TrackNumber",
                table: "MediaItems");
        }
    }
}
