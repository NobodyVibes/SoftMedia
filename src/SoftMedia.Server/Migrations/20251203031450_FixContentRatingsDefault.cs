using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SoftMedia.Server.Migrations
{
    /// <inheritdoc />
    public partial class FixContentRatingsDefault : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("UPDATE Users SET ContentRatings = '{}' WHERE ContentRatings = '' OR ContentRatings IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {

        }
    }
}
