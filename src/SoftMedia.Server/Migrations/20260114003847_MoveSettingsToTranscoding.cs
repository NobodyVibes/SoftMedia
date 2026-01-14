using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SoftMedia.Server.Migrations
{
    /// <inheritdoc />
    public partial class MoveSettingsToTranscoding : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Move transcoding-related settings from Streaming to Transcoding group
            migrationBuilder.Sql(@"
                UPDATE Settings SET ""Group"" = 'Transcoding' WHERE Key IN (
                    'MaxSimultaneousTranscodes', 
                    'OutputVideoCodec', 
                    'PreserveHDR'
                );
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {

        }
    }
}
