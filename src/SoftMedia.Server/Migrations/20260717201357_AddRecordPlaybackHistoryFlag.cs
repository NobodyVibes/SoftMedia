using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SoftMedia.Server.Migrations
{
    /// <inheritdoc />
    public partial class AddRecordPlaybackHistoryFlag : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Hand-edited from the scaffolded defaultValue: false — existing users must default
            // to RECORDING (true); the scaffolder's false would silently disable history for
            // every pre-existing account. This default only backfills existing rows during the
            // ALTER TABLE; the app always writes the column explicitly on insert.
            migrationBuilder.AddColumn<bool>(
                name: "RecordPlaybackHistory",
                table: "Users",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RecordPlaybackHistory",
                table: "Users");
        }
    }
}
