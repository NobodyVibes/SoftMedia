using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SoftMedia.Server.Migrations
{
    /// <inheritdoc />
    public partial class AddMediaQualityMetadata : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "AudioChannels",
                table: "MediaItems",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AudioTracksJson",
                table: "MediaItems",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "BitDepth",
                table: "MediaItems",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "Bitrate",
                table: "MediaItems",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "FrameRate",
                table: "MediaItems",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "HdrFormat",
                table: "MediaItems",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Height",
                table: "MediaItems",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SubtitleTracksJson",
                table: "MediaItems",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Width",
                table: "MediaItems",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AudioChannels",
                table: "MediaItems");

            migrationBuilder.DropColumn(
                name: "AudioTracksJson",
                table: "MediaItems");

            migrationBuilder.DropColumn(
                name: "BitDepth",
                table: "MediaItems");

            migrationBuilder.DropColumn(
                name: "Bitrate",
                table: "MediaItems");

            migrationBuilder.DropColumn(
                name: "FrameRate",
                table: "MediaItems");

            migrationBuilder.DropColumn(
                name: "HdrFormat",
                table: "MediaItems");

            migrationBuilder.DropColumn(
                name: "Height",
                table: "MediaItems");

            migrationBuilder.DropColumn(
                name: "SubtitleTracksJson",
                table: "MediaItems");

            migrationBuilder.DropColumn(
                name: "Width",
                table: "MediaItems");
        }
    }
}
