using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SoftMedia.Server.Migrations
{
    /// <inheritdoc />
    public partial class AddPlaybackPosition : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "PlaybackPosition",
                table: "UserMediaInteractions",
                type: "REAL",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PlaybackPosition",
                table: "UserMediaInteractions");
        }
    }
}
