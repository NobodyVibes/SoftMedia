using Microsoft.EntityFrameworkCore.Migrations;
using SoftMedia.Server.Models;

#nullable disable

namespace SoftMedia.Server.Migrations
{
    /// <inheritdoc />
    public partial class RenameDisableTranscoding : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Update key and invert value (true <-> false) for existing settings
            migrationBuilder.Sql(
                @"UPDATE Settings 
                  SET Key = 'EnableTranscoding', 
                      Value = CASE WHEN Value = 'true' THEN 'false' ELSE 'true' END,
                      Description = 'Enable video transcoding. If disabled, files will be served directly.'
                  WHERE Key = 'DisableTranscoding'");

            // Insert default if it doesn't exist (e.g. fresh DB)
            // Note: Standard EF Core seeding in SettingsService handles this, but migration ensures data integrity
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                @"UPDATE Settings 
                  SET Key = 'DisableTranscoding', 
                      Value = CASE WHEN Value = 'true' THEN 'false' ELSE 'true' END,
                      Description = 'Skip video conversion and serve files directly. May cause playback issues if the client doesn''t support the media format.'
                  WHERE Key = 'EnableTranscoding'");
        }
    }
}
