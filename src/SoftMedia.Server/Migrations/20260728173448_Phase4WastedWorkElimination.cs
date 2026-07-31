using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SoftMedia.Server.Migrations
{
    /// <inheritdoc />
    public partial class Phase4WastedWorkElimination : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "AmnestyCount",
                table: "MediaItems",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "GameMode",
                table: "MediaItems",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "GamePlatform",
                table: "MediaItems",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastProbeAttemptUtc",
                table: "MediaItems",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "NextAmnestyUtc",
                table: "MediaItems",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SeriesStatus",
                table: "MediaItems",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ProviderLookupCache",
                columns: table => new
                {
                    Provider = table.Column<string>(type: "TEXT", nullable: false),
                    QueryKey = table.Column<string>(type: "TEXT", nullable: false),
                    LastAttemptUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    AttemptCount = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProviderLookupCache", x => new { x.Provider, x.QueryKey });
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ProviderLookupCache");

            migrationBuilder.DropColumn(
                name: "AmnestyCount",
                table: "MediaItems");

            migrationBuilder.DropColumn(
                name: "GameMode",
                table: "MediaItems");

            migrationBuilder.DropColumn(
                name: "GamePlatform",
                table: "MediaItems");

            migrationBuilder.DropColumn(
                name: "LastProbeAttemptUtc",
                table: "MediaItems");

            migrationBuilder.DropColumn(
                name: "NextAmnestyUtc",
                table: "MediaItems");

            migrationBuilder.DropColumn(
                name: "SeriesStatus",
                table: "MediaItems");
        }
    }
}
