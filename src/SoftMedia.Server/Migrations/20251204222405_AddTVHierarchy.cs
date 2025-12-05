using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SoftMedia.Server.Migrations
{
    /// <inheritdoc />
    public partial class AddTVHierarchy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "EpisodeNumber",
                table: "MediaItems",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SeasonNumber",
                table: "MediaItems",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "SeriesId",
                table: "MediaItems",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Type",
                table: "MediaItems",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateIndex(
                name: "IX_MediaItems_SeriesId",
                table: "MediaItems",
                column: "SeriesId");

            migrationBuilder.AddForeignKey(
                name: "FK_MediaItems_MediaItems_SeriesId",
                table: "MediaItems",
                column: "SeriesId",
                principalTable: "MediaItems",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_MediaItems_MediaItems_SeriesId",
                table: "MediaItems");

            migrationBuilder.DropIndex(
                name: "IX_MediaItems_SeriesId",
                table: "MediaItems");

            migrationBuilder.DropColumn(
                name: "EpisodeNumber",
                table: "MediaItems");

            migrationBuilder.DropColumn(
                name: "SeasonNumber",
                table: "MediaItems");

            migrationBuilder.DropColumn(
                name: "SeriesId",
                table: "MediaItems");

            migrationBuilder.DropColumn(
                name: "Type",
                table: "MediaItems");
        }
    }
}
