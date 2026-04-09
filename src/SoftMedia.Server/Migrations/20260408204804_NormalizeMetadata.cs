using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SoftMedia.Server.Migrations
{
    /// <inheritdoc />
    public partial class NormalizeMetadata : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_MediaItemCasts_MediaItems_MediaItemId1",
                table: "MediaItemCasts");

            migrationBuilder.DropForeignKey(
                name: "FK_MediaItemGenres_MediaItems_MediaItemId1",
                table: "MediaItemGenres");

            migrationBuilder.DropIndex(
                name: "IX_MediaItemGenres_MediaItemId1",
                table: "MediaItemGenres");

            migrationBuilder.DropPrimaryKey(
                name: "PK_MediaItemCasts",
                table: "MediaItemCasts");

            migrationBuilder.DropIndex(
                name: "IX_MediaItemCasts_MediaItemId1",
                table: "MediaItemCasts");

            migrationBuilder.DropColumn(
                name: "AudioTracksJson",
                table: "MediaItems");

            migrationBuilder.DropColumn(
                name: "MediaItemId1",
                table: "MediaItemGenres");

            migrationBuilder.DropColumn(
                name: "MediaItemId1",
                table: "MediaItemCasts");

            migrationBuilder.RenameColumn(
                name: "SubtitleTracksJson",
                table: "MediaItems",
                newName: "MetadataHash");

            migrationBuilder.AddColumn<DateTime>(
                name: "LastScannedUtc",
                table: "MediaItems",
                type: "TEXT",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.AlterColumn<string>(
                name: "Character",
                table: "MediaItemCasts",
                type: "TEXT",
                maxLength: 256,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "TEXT",
                oldMaxLength: 256);

            migrationBuilder.AddColumn<int>(
                name: "Id",
                table: "MediaItemCasts",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0)
                .Annotation("Sqlite:Autoincrement", true);

            migrationBuilder.AddPrimaryKey(
                name: "PK_MediaItemCasts",
                table: "MediaItemCasts",
                column: "Id");

            migrationBuilder.CreateTable(
                name: "AudioTracks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    MediaItemId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Index = table.Column<int>(type: "INTEGER", nullable: false),
                    Codec = table.Column<string>(type: "TEXT", nullable: true),
                    Language = table.Column<string>(type: "TEXT", nullable: true),
                    Channels = table.Column<int>(type: "INTEGER", nullable: false),
                    ChannelLayout = table.Column<string>(type: "TEXT", nullable: true),
                    Title = table.Column<string>(type: "TEXT", nullable: true),
                    IsDefault = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AudioTracks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AudioTracks_MediaItems_MediaItemId",
                        column: x => x.MediaItemId,
                        principalTable: "MediaItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Chapters",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    MediaItemId = table.Column<Guid>(type: "TEXT", nullable: false),
                    StartTime = table.Column<double>(type: "REAL", nullable: false),
                    Title = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Chapters", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Chapters_MediaItems_MediaItemId",
                        column: x => x.MediaItemId,
                        principalTable: "MediaItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SubtitleTracks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    MediaItemId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Index = table.Column<int>(type: "INTEGER", nullable: false),
                    Codec = table.Column<string>(type: "TEXT", nullable: true),
                    Language = table.Column<string>(type: "TEXT", nullable: true),
                    Title = table.Column<string>(type: "TEXT", nullable: true),
                    IsDefault = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsForced = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SubtitleTracks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SubtitleTracks_MediaItems_MediaItemId",
                        column: x => x.MediaItemId,
                        principalTable: "MediaItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MediaItemCasts_MediaItemId",
                table: "MediaItemCasts",
                column: "MediaItemId");

            migrationBuilder.CreateIndex(
                name: "IX_AudioTracks_MediaItemId",
                table: "AudioTracks",
                column: "MediaItemId");

            migrationBuilder.CreateIndex(
                name: "IX_Chapters_MediaItemId",
                table: "Chapters",
                column: "MediaItemId");

            migrationBuilder.CreateIndex(
                name: "IX_SubtitleTracks_MediaItemId",
                table: "SubtitleTracks",
                column: "MediaItemId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AudioTracks");

            migrationBuilder.DropTable(
                name: "Chapters");

            migrationBuilder.DropTable(
                name: "SubtitleTracks");

            migrationBuilder.DropPrimaryKey(
                name: "PK_MediaItemCasts",
                table: "MediaItemCasts");

            migrationBuilder.DropIndex(
                name: "IX_MediaItemCasts_MediaItemId",
                table: "MediaItemCasts");

            migrationBuilder.DropColumn(
                name: "LastScannedUtc",
                table: "MediaItems");

            migrationBuilder.DropColumn(
                name: "Id",
                table: "MediaItemCasts");

            migrationBuilder.RenameColumn(
                name: "MetadataHash",
                table: "MediaItems",
                newName: "SubtitleTracksJson");

            migrationBuilder.AddColumn<string>(
                name: "AudioTracksJson",
                table: "MediaItems",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "MediaItemId1",
                table: "MediaItemGenres",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "Character",
                table: "MediaItemCasts",
                type: "TEXT",
                maxLength: 256,
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "TEXT",
                oldMaxLength: 256,
                oldNullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "MediaItemId1",
                table: "MediaItemCasts",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddPrimaryKey(
                name: "PK_MediaItemCasts",
                table: "MediaItemCasts",
                columns: new[] { "MediaItemId", "PersonId", "Character" });

            migrationBuilder.CreateIndex(
                name: "IX_MediaItemGenres_MediaItemId1",
                table: "MediaItemGenres",
                column: "MediaItemId1");

            migrationBuilder.CreateIndex(
                name: "IX_MediaItemCasts_MediaItemId1",
                table: "MediaItemCasts",
                column: "MediaItemId1");

            migrationBuilder.AddForeignKey(
                name: "FK_MediaItemCasts_MediaItems_MediaItemId1",
                table: "MediaItemCasts",
                column: "MediaItemId1",
                principalTable: "MediaItems",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_MediaItemGenres_MediaItems_MediaItemId1",
                table: "MediaItemGenres",
                column: "MediaItemId1",
                principalTable: "MediaItems",
                principalColumn: "Id");
        }
    }
}
