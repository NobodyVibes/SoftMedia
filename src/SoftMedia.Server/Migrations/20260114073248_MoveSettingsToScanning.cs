using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SoftMedia.Server.Migrations
{
    /// <inheritdoc />
    public partial class MoveSettingsToScanning : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Move library scanning settings to Scanning group
            migrationBuilder.Sql(@"
                UPDATE Settings SET ""Group"" = 'Scanning' WHERE Key IN (
                    'EnableFileWatcher',
                    'MetadataRefreshIntervalHours'
                );
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {

        }
    }
}
