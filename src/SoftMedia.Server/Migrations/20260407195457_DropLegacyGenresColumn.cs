using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SoftMedia.Server.Migrations
{
    /// <inheritdoc />
    public partial class DropLegacyGenresColumn : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Genres",
                table: "MediaItems");

            migrationBuilder.AddColumn<Guid>(
                name: "MediaItemId1",
                table: "MediaItemGenres",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "MediaItemId1",
                table: "MediaItemCasts",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_MediaItemGenres_MediaItemId1",
                table: "MediaItemGenres",
                column: "MediaItemId1");

            migrationBuilder.CreateIndex(
                name: "IX_MediaItemCasts_MediaItemId1",
                table: "MediaItemCasts",
                column: "MediaItemId1");

            migrationBuilder.AddForeignKey(
                name: "FK_MediaItemCasts_MediaItems_MediaItemId1",
                table: "MediaItemCasts",
                column: "MediaItemId1",
                principalTable: "MediaItems",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_MediaItemGenres_MediaItems_MediaItemId1",
                table: "MediaItemGenres",
                column: "MediaItemId1",
                principalTable: "MediaItems",
                principalColumn: "Id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_MediaItemCasts_MediaItems_MediaItemId1",
                table: "MediaItemCasts");

            migrationBuilder.DropForeignKey(
                name: "FK_MediaItemGenres_MediaItems_MediaItemId1",
                table: "MediaItemGenres");

            migrationBuilder.DropIndex(
                name: "IX_MediaItemGenres_MediaItemId1",
                table: "MediaItemGenres");

            migrationBuilder.DropIndex(
                name: "IX_MediaItemCasts_MediaItemId1",
                table: "MediaItemCasts");

            migrationBuilder.DropColumn(
                name: "MediaItemId1",
                table: "MediaItemGenres");

            migrationBuilder.DropColumn(
                name: "MediaItemId1",
                table: "MediaItemCasts");

            migrationBuilder.AddColumn<string>(
                name: "Genres",
                table: "MediaItems",
                type: "TEXT",
                nullable: true);
        }
    }
}
