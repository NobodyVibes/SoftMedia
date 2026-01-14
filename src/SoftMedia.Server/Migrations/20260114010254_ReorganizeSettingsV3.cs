using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SoftMedia.Server.Migrations
{
    /// <inheritdoc />
    public partial class ReorganizeSettingsV3 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Move quality/output settings to Streaming (client-facing)
            migrationBuilder.Sql(@"
                UPDATE Settings SET ""Group"" = 'Streaming' WHERE Key IN (
                    'MaxTranscodeResolution',
                    'TranscodeCRF',
                    'PreserveHDR',
                    'DisableTranscoding'
                );
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {

        }
    }
}
