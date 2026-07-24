using Microsoft.EntityFrameworkCore;
using SoftMedia.Server.Models;
using SoftMedia.Server.Tests.Helpers;
using Xunit;

namespace SoftMedia.Server.Tests.Integration;

/// SR-WI-062 — catalog hot-path indexes. The factory boots the production
/// startup path, so DbInitializer runs the REAL migration chain (including
/// AddCatalogHotPathIndexes) against real shared-cache SQLite — these tests
/// therefore exercise the migrated schema, not an EnsureCreated shortcut.
public class CatalogIndexIntegrationTests : IntegrationTestBase
{
    private async Task<Library> SeedLibraryAsync()
    {
        return await Factory.WithDbAsync(async db =>
        {
            var lib = new Library { Name = "IndexTestLib", Type = LibraryType.Movie, Paths = new() { @"C:\nope" } };
            db.Libraries.Add(lib);
            await db.SaveChangesAsync();
            return lib;
        });
    }

    [Fact]
    public async Task DuplicatePath_FileBackedInsert_IsRejectedByUniqueIndex()
    {
        var lib = await SeedLibraryAsync();
        const string path = @"C:\Media\unique-path-test\movie.mkv";

        await Factory.WithDbAsync(async db =>
        {
            db.MediaItems.Add(new MediaItem { LibraryId = lib.Id, Title = "First", Path = path, Type = MediaType.Movie });
            await db.SaveChangesAsync();
        });

        var ex = await Assert.ThrowsAsync<DbUpdateException>(() =>
            Factory.WithDbAsync(async db =>
            {
                db.MediaItems.Add(new MediaItem { LibraryId = lib.Id, Title = "Dupe", Path = path, Type = MediaType.Movie });
                await db.SaveChangesAsync();
            }));

        Assert.Contains("UNIQUE", ex.InnerException?.Message ?? ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ContainerRows_SharingFolderPath_AreStillAllowed()
    {
        // Container types share their folder Path by design — TvScanner creates
        // every Season with Path = series.Path (TvScanner.EnsureSeasonAsync).
        // The unique index is partial (file-backed types only) so this MUST work.
        var lib = await SeedLibraryAsync();
        const string folder = @"C:\Media\flat-pack-show";

        await Factory.WithDbAsync(async db =>
        {
            var series = new MediaItem { LibraryId = lib.Id, Title = "Show", Path = folder, Type = MediaType.Series };
            db.MediaItems.Add(series);
            db.MediaItems.Add(new MediaItem
            {
                LibraryId = lib.Id, SeriesId = series.Id, Title = "Season 1",
                Path = folder, Type = MediaType.Season, SeasonNumber = 1,
            });
            db.MediaItems.Add(new MediaItem
            {
                LibraryId = lib.Id, SeriesId = series.Id, Title = "Season 2",
                Path = folder, Type = MediaType.Season, SeasonNumber = 2,
            });
            await db.SaveChangesAsync();
        });
    }

    [Fact]
    public async Task CaseVariantPath_Insert_Succeeds_BinaryCollationIsDocumentedBehavior()
    {
        // SQLite's default BINARY collation makes the unique Path index case-
        // sensitive. This pins the documented trade-off (scanners normalize
        // lookups case-insensitively, so this never happens in practice).
        var lib = await SeedLibraryAsync();

        await Factory.WithDbAsync(async db =>
        {
            db.MediaItems.Add(new MediaItem { LibraryId = lib.Id, Title = "Lower", Path = @"c:\media\case-test\a.mkv", Type = MediaType.Movie });
            db.MediaItems.Add(new MediaItem { LibraryId = lib.Id, Title = "Upper", Path = @"C:\MEDIA\CASE-TEST\A.MKV", Type = MediaType.Movie });
            await db.SaveChangesAsync();
        });
    }

    [Theory]
    [InlineData("MediaItems", "IX_MediaItems_LibraryId_Type")]
    [InlineData("MediaItems", "IX_MediaItems_Type_DateAdded")]
    [InlineData("MediaItems", "IX_MediaItems_Title")]
    [InlineData("MediaItems", "IX_MediaItems_Year")]
    [InlineData("MediaItems", "IX_MediaItems_Path")]
    [InlineData("MediaItems", "IX_MediaItems_Path_UniqueFileBacked")]
    [InlineData("UserMediaInteractions", "IX_UserMediaInteractions_UserId_LastPlayed")]
    public async Task HotPathIndex_ExistsInMigratedSchema(string table, string index)
    {
        var count = await Factory.WithDbAsync(async db =>
        {
            await using var cmd = db.Database.GetDbConnection().CreateCommand();
            await db.Database.OpenConnectionAsync();
            cmd.CommandText =
                "SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND tbl_name = $table AND name = $index";
            var pTable = cmd.CreateParameter(); pTable.ParameterName = "$table"; pTable.Value = table; cmd.Parameters.Add(pTable);
            var pIndex = cmd.CreateParameter(); pIndex.ParameterName = "$index"; pIndex.Value = index; cmd.Parameters.Add(pIndex);
            return Convert.ToInt32(await cmd.ExecuteScalarAsync());
        });

        Assert.Equal(1, count);
    }
}
