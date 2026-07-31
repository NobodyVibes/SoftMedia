using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SoftMedia.Server.Migrations
{
    /// <summary>
    /// SM-WI-052 — expression index serving the case-insensitive path lookups.
    /// The watcher/single-file/TV/music paths query `m.Path.ToLower() == lowered`,
    /// which EF translates to `lower("Path") = @p` — unindexable by the plain Path
    /// index, so every watcher import did a full MediaItems scan. An expression index
    /// on lower("Path") matches that shape exactly. (A COLLATE NOCASE rewrite was
    /// rejected: EF.Functions.Collate cannot be evaluated by the InMemory provider the
    /// unit suite runs on, and SQLite's lower()/NOCASE are equally ASCII-only — the
    /// non-ASCII casing limitation is unchanged and documented at the query sites.)
    /// Raw SQL: EF's model has no concept of expression indexes.
    /// </summary>
    public partial class Phase5PathLowerIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """CREATE INDEX IF NOT EXISTS "IX_MediaItems_Path_Lower" ON "MediaItems" (lower("Path"));""");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""DROP INDEX IF EXISTS "IX_MediaItems_Path_Lower";""");
        }
    }
}
