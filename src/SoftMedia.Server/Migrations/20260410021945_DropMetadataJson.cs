using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SoftMedia.Server.Migrations
{
    /// <inheritdoc />
    public partial class DropMetadataJson : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MetadataJson",
                table: "MediaItems");

            migrationBuilder.AddColumn<double>(
                name: "CreditsStart",
                table: "MediaItems",
                type: "REAL",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ProviderMetadataCaches",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    MediaItemId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProviderId = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    RawPayload = table.Column<string>(type: "TEXT", nullable: false),
                    LastUpdated = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProviderMetadataCaches", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProviderMetadataCaches_MediaItems_MediaItemId",
                        column: x => x.MediaItemId,
                        principalTable: "MediaItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ProviderMetadataCaches_MediaItemId",
                table: "ProviderMetadataCaches",
                column: "MediaItemId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ProviderMetadataCaches");

            migrationBuilder.DropColumn(
                name: "CreditsStart",
                table: "MediaItems");

            migrationBuilder.AddColumn<string>(
                name: "MetadataJson",
                table: "MediaItems",
                type: "TEXT",
                nullable: true);
        }
    }
}
