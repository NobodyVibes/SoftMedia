using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SoftMedia.Server.Migrations
{
    /// <inheritdoc />
    public partial class RemoveMediaTypeTrack : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Remap any MediaItems with the removed Track (10) type to Audio (3).
            // MediaType.Track was never assigned by any scanner, but this ensures
            // any legacy or test data is safely remapped.
            migrationBuilder.Sql(@"
                UPDATE MediaItems SET Type = 3 WHERE Type = 10;
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // No rollback needed — Track was functionally identical to Audio.
            // Re-adding the enum value doesn't require a data migration.
        }
    }
}
