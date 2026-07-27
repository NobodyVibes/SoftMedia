using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SoftMedia.Server.Migrations
{
    /// <inheritdoc />
    public partial class AddPlaylistCoverImage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CoverImagePath",
                table: "Playlists",
                type: "TEXT",
                maxLength: 400,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CoverImagePath",
                table: "Playlists");
        }
    }
}
