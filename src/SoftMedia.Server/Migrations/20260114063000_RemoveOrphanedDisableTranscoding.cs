using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SoftMedia.Server.Migrations
{
    /// <inheritdoc />
    public partial class RemoveOrphanedDisableTranscoding : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Delete the old setting if it still exists (orphaned)
            migrationBuilder.Sql("DELETE FROM Settings WHERE Key = 'DisableTranscoding'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Re-inserting it in Down is probably excessive, but for correctness:
            // migrationBuilder.Sql("INSERT INTO Settings (Key, Value, Group, Description) VALUES ('DisableTranscoding', 'false', 'Transcoding', 'Skip video conversion and serve files directly. May cause playback issues if the client doesn''t support the media format.')");
        }
    }
}
