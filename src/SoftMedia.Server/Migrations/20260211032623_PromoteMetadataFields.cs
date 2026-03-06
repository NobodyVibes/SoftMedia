using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SoftMedia.Server.Migrations
{
    /// <inheritdoc />
    public partial class PromoteMetadataFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Director",
                table: "MediaItems",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Genres",
                table: "MediaItems",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Studio",
                table: "MediaItems",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Director",
                table: "MediaItems");

            migrationBuilder.DropColumn(
                name: "Genres",
                table: "MediaItems");

            migrationBuilder.DropColumn(
                name: "Studio",
                table: "MediaItems");
        }
    }
}
