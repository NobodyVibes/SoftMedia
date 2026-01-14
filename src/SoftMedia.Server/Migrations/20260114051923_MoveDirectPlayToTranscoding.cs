using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SoftMedia.Server.Migrations
{
    /// <inheritdoc />
    public partial class MoveDirectPlayToTranscoding : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Move direct play settings to Transcoding
            migrationBuilder.Sql(@"
                UPDATE Settings SET ""Group"" = 'Transcoding' WHERE Key IN (
                    'DisableTranscoding',
                    'ForceDirectPlayWhenPossible'
                );
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {

        }
    }
}
