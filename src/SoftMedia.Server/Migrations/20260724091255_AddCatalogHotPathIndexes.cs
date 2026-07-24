using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SoftMedia.Server.Migrations
{
    /// <inheritdoc />
    /// <remarks>
    /// SR-WI-062 — catalog hot-path indexes, including a UNIQUE index on
    /// MediaItems.Path for FILE-BACKED rows.
    ///
    /// Why the unique index is PARTIAL (filter: Type NOT IN (1, 7, 8, 9, 10)
    /// AND Path &lt;&gt; ''): container rows — Series(1), Season(7), Artist(8),
    /// Album(9), ComicSeries(10), see BaseMediaScanner.ContainerTypes — share
    /// their folder Path by design (TvScanner.EnsureSeasonAsync sets
    /// season.Path = series.Path), so a blanket unique index would both reject
    /// legitimate scanner inserts and force this migration to delete valid
    /// container rows. File-backed types (Movie 0, Episode 2, Audio 3, Book 4,
    /// Game 5, Photo 6, ComicIssue 11) use Path as identity, and SR-WI-030
    /// closed the duplicate-creation races — so among them a duplicate
    /// non-empty Path is corruption. Empty Path is excluded because it is not
    /// a file identity (scanners always set a real path; only test fixtures
    /// leave the "" default). The separate non-unique IX_MediaItems_Path
    /// serves generic Path lookups (SQLite only consults a partial index when
    /// the query implies its filter).
    ///
    /// Self-healing dedup: creating the unique index fails if historical
    /// duplicates persist, so Up() first soft-resolves them (SQLite only; the
    /// InMemory test provider ignores relational migrations anyway). For each
    /// duplicated non-empty Path among file-backed rows, keep exactly one row,
    /// ranked by:
    ///   1. rows referenced by user data (PlaybackHistory or
    ///      UserMediaInteractions) win over unreferenced rows;
    ///   2. then the earliest DateAdded (the original import);
    ///   3. then lowest Id as a deterministic tie-break.
    /// Losing rows are leaf types (never parents in the MediaItems hierarchy),
    /// so deleting them only cascades their own child rows (tracks, chapters,
    /// history, bookmarks, …) — by rule 1 that sacrifices user data only when
    /// BOTH duplicates carry some.
    ///
    /// Collation note: SQLite's default BINARY collation makes the unique index
    /// case-SENSITIVE — 'C:\a.mkv' and 'c:\A.MKV' both survive it. Acceptable:
    /// the scanners normalize Path lookups case-insensitively, so case-variant
    /// duplicates are not created going forward.
    /// </remarks>
    public partial class AddCatalogHotPathIndexes : Migration
    {
        private const string SqliteProvider = "Microsoft.EntityFrameworkCore.Sqlite";

        /// <summary>File-backed MediaType values; keep in sync with the index
        /// filter below and the fluent config in AppDbContext.</summary>
        private const string FileBackedTypes = "(0, 2, 3, 4, 5, 6, 11)";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            if (migrationBuilder.ActiveProvider == SqliteProvider)
            {
                // Rank every file-backed row that shares its non-empty Path with
                // another file-backed row. rn = 1 is the keeper; rn > 1 lose.
                migrationBuilder.Sql($"""
                    CREATE TEMP TABLE "__sr062_dup_ranked" AS
                    SELECT m."Id" AS "Id",
                           ROW_NUMBER() OVER (
                               PARTITION BY m."Path"
                               ORDER BY
                                   CASE WHEN EXISTS (SELECT 1 FROM "PlaybackHistory" ph
                                                     WHERE ph."MediaItemId" = m."Id")
                                          OR EXISTS (SELECT 1 FROM "UserMediaInteractions" umi
                                                     WHERE umi."MediaItemId" = m."Id")
                                        THEN 0 ELSE 1 END,
                                   m."DateAdded",
                                   m."Id"
                           ) AS "rn"
                    FROM "MediaItems" m
                    WHERE m."Type" IN {FileBackedTypes}
                      AND m."Path" <> ''
                      AND m."Path" IN (SELECT "Path" FROM "MediaItems"
                                       WHERE "Type" IN {FileBackedTypes}
                                       GROUP BY "Path" HAVING COUNT(*) > 1);
                    """);

                // Delete the losers; their child rows follow via FK cascade.
                migrationBuilder.Sql("""
                    DELETE FROM "MediaItems"
                    WHERE "Id" IN (SELECT "Id" FROM "__sr062_dup_ranked" WHERE "rn" > 1);
                    """);

                migrationBuilder.Sql("""DROP TABLE "__sr062_dup_ranked";""");
            }

            migrationBuilder.CreateIndex(
                name: "IX_UserMediaInteractions_UserId_LastPlayed",
                table: "UserMediaInteractions",
                columns: new[] { "UserId", "LastPlayed" });

            migrationBuilder.CreateIndex(
                name: "IX_MediaItems_LibraryId_Type",
                table: "MediaItems",
                columns: new[] { "LibraryId", "Type" });

            migrationBuilder.CreateIndex(
                name: "IX_MediaItems_Path",
                table: "MediaItems",
                column: "Path");

            migrationBuilder.CreateIndex(
                name: "IX_MediaItems_Path_UniqueFileBacked",
                table: "MediaItems",
                column: "Path",
                unique: true,
                filter: "\"Type\" NOT IN (1, 7, 8, 9, 10) AND \"Path\" <> ''");

            migrationBuilder.CreateIndex(
                name: "IX_MediaItems_Title",
                table: "MediaItems",
                column: "Title");

            migrationBuilder.CreateIndex(
                name: "IX_MediaItems_Type_DateAdded",
                table: "MediaItems",
                columns: new[] { "Type", "DateAdded" });

            migrationBuilder.CreateIndex(
                name: "IX_MediaItems_Year",
                table: "MediaItems",
                column: "Year");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_UserMediaInteractions_UserId_LastPlayed",
                table: "UserMediaInteractions");

            migrationBuilder.DropIndex(
                name: "IX_MediaItems_LibraryId_Type",
                table: "MediaItems");

            migrationBuilder.DropIndex(
                name: "IX_MediaItems_Path",
                table: "MediaItems");

            migrationBuilder.DropIndex(
                name: "IX_MediaItems_Path_UniqueFileBacked",
                table: "MediaItems");

            migrationBuilder.DropIndex(
                name: "IX_MediaItems_Title",
                table: "MediaItems");

            migrationBuilder.DropIndex(
                name: "IX_MediaItems_Type_DateAdded",
                table: "MediaItems");

            migrationBuilder.DropIndex(
                name: "IX_MediaItems_Year",
                table: "MediaItems");
        }
    }
}
