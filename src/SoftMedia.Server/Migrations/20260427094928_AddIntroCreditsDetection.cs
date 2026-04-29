using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SoftMedia.Server.Migrations
{
    /// <inheritdoc />
    public partial class AddIntroCreditsDetection : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "CreditsEnd",
                table: "MediaItems",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "CreditsSource",
                table: "MediaItems",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "IntroEnd",
                table: "MediaItems",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "IntroSource",
                table: "MediaItems",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "IntroStart",
                table: "MediaItems",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastIntroDetectionUtc",
                table: "MediaItems",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "MediaFingerprints",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    MediaItemId = table.Column<Guid>(type: "TEXT", nullable: false),
                    HeadFingerprint = table.Column<byte[]>(type: "BLOB", nullable: true),
                    HeadDurationSeconds = table.Column<double>(type: "REAL", nullable: false),
                    TailFingerprint = table.Column<byte[]>(type: "BLOB", nullable: true),
                    TailDurationSeconds = table.Column<double>(type: "REAL", nullable: false),
                    GeneratedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MediaFingerprints", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MediaFingerprints_MediaItems_MediaItemId",
                        column: x => x.MediaItemId,
                        principalTable: "MediaItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MediaFingerprints_MediaItemId",
                table: "MediaFingerprints",
                column: "MediaItemId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MediaFingerprints");

            migrationBuilder.DropColumn(
                name: "CreditsEnd",
                table: "MediaItems");

            migrationBuilder.DropColumn(
                name: "CreditsSource",
                table: "MediaItems");

            migrationBuilder.DropColumn(
                name: "IntroEnd",
                table: "MediaItems");

            migrationBuilder.DropColumn(
                name: "IntroSource",
                table: "MediaItems");

            migrationBuilder.DropColumn(
                name: "IntroStart",
                table: "MediaItems");

            migrationBuilder.DropColumn(
                name: "LastIntroDetectionUtc",
                table: "MediaItems");
        }
    }
}
