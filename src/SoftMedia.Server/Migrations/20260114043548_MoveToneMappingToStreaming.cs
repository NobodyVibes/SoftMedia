using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SoftMedia.Server.Migrations
{
    /// <inheritdoc />
    public partial class MoveToneMappingToStreaming : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Move ToneMappingAlgorithm to Streaming (pairs with PreserveHDR)
            migrationBuilder.Sql(@"
                UPDATE Settings SET ""Group"" = 'Streaming' WHERE Key = 'ToneMappingAlgorithm';
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {

        }
    }
}
