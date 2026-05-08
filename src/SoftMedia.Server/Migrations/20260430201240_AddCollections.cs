using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SoftMedia.Server.Migrations
{
    /// <inheritdoc />
    public partial class AddCollections : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "CollectionId",
                table: "MediaItems",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "CollectionLookupAttempted",
                table: "MediaItems",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Collections",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Overview = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    PosterUrl = table.Column<string>(type: "TEXT", nullable: true),
                    WikidataId = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Collections", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MediaItems_CollectionId",
                table: "MediaItems",
                column: "CollectionId");

            migrationBuilder.CreateIndex(
                name: "IX_Collections_WikidataId",
                table: "Collections",
                column: "WikidataId",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_MediaItems_Collections_CollectionId",
                table: "MediaItems",
                column: "CollectionId",
                principalTable: "Collections",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_MediaItems_Collections_CollectionId",
                table: "MediaItems");

            migrationBuilder.DropTable(
                name: "Collections");

            migrationBuilder.DropIndex(
                name: "IX_MediaItems_CollectionId",
                table: "MediaItems");

            migrationBuilder.DropColumn(
                name: "CollectionId",
                table: "MediaItems");

            migrationBuilder.DropColumn(
                name: "CollectionLookupAttempted",
                table: "MediaItems");
        }
    }
}
