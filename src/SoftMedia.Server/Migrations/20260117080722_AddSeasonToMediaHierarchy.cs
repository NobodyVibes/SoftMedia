using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SoftMedia.Server.Migrations
{
    /// <inheritdoc />
    public partial class AddSeasonToMediaHierarchy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "SeasonId",
                table: "MediaItems",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_MediaItems_SeasonId",
                table: "MediaItems",
                column: "SeasonId");

            migrationBuilder.AddForeignKey(
                name: "FK_MediaItems_MediaItems_SeasonId",
                table: "MediaItems",
                column: "SeasonId",
                principalTable: "MediaItems",
                principalColumn: "Id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_MediaItems_MediaItems_SeasonId",
                table: "MediaItems");

            migrationBuilder.DropIndex(
                name: "IX_MediaItems_SeasonId",
                table: "MediaItems");

            migrationBuilder.DropColumn(
                name: "SeasonId",
                table: "MediaItems");
        }
    }
}
