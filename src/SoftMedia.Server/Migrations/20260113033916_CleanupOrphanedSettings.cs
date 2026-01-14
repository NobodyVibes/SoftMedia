using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SoftMedia.Server.Migrations
{
    /// <inheritdoc />
    public partial class CleanupOrphanedSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Delete orphaned settings that are no longer used
            migrationBuilder.Sql(@"
                DELETE FROM Settings WHERE Key IN (
                    'ServerName', 'Language', 'LogLevel', 
                    'EnableRemoteAccess', 'SecureConnections', 
                    'DailyRescan', 'AutoSelectSubtitle', 'AutoRefreshMetadata'
                );
            ");
            
            // Rename RealTimeMonitoring to EnableFileWatcher and move to Metadata group
            migrationBuilder.Sql(@"
                UPDATE Settings 
                SET Key = 'EnableFileWatcher', 
                    [Group] = 'Metadata',
                    Description = 'Automatically detect new files and update library. Disable for manual scanning only.'
                WHERE Key = 'RealTimeMonitoring';
            ");
            
            // Move transcoding-related settings from Streaming to Transcoding group
            migrationBuilder.Sql(@"
                UPDATE Settings SET [Group] = 'Transcoding' WHERE Key IN (
                    'MaxSimultaneousTranscodes', 
                    'OutputVideoCodec', 
                    'PreserveHDR'
                );
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Revert EnableFileWatcher back to RealTimeMonitoring
            migrationBuilder.Sql(@"
                UPDATE Settings 
                SET Key = 'RealTimeMonitoring', 
                    [Group] = 'Scanning',
                    Description = 'Use FileSystemWatcher to detect changes instantly.'
                WHERE Key = 'EnableFileWatcher';
            ");
            
            // Note: We don't restore deleted settings in Down - they would be recreated by InitializeDefaultsAsync
        }
    }
}

