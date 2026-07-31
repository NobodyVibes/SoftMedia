using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SoftMedia.Server.Migrations
{
    /// <inheritdoc />
    public partial class AddVersionGroups : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "PreferredVersion",
                table: "MediaItems",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<Guid>(
                name: "VersionGroupId",
                table: "MediaItems",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_MediaItems_VersionGroupId",
                table: "MediaItems",
                column: "VersionGroupId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_MediaItems_VersionGroupId",
                table: "MediaItems");

            migrationBuilder.DropColumn(
                name: "PreferredVersion",
                table: "MediaItems");

            migrationBuilder.DropColumn(
                name: "VersionGroupId",
                table: "MediaItems");
        }
    }
}
