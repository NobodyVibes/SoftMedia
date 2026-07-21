using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SoftMedia.Server.Data;
using SoftMedia.Server.Models;
using SoftMedia.Server.Services.Media;
using SoftMedia.Server.Services.Metadata;
using Xunit;

namespace SoftMedia.Server.Tests.Services.Media;

/// Verifies the post-restore artwork repair: it re-queues only items whose cached
/// /cache/ image file is actually missing, maps episode/season art to the parent
/// series, honours the metadata lock, and leaves present files / external URLs alone.
public class ArtworkRepairServiceTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _webRoot;
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;

    public ArtworkRepairServiceTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "sm-artrepair-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
        _webRoot = Path.Combine(_tempRoot, "wwwroot");
        Directory.CreateDirectory(Path.Combine(_webRoot, "cache", "images", "movies"));

        _connection = new SqliteConnection($"Data Source={Path.Combine(_tempRoot, "t.db")}");
        _connection.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;
        using var ctx = new AppDbContext(_options);
        ctx.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _connection.Dispose();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_tempRoot, true); } catch { }
    }

    // Captures every enqueue so the test can assert exactly which items were re-queued.
    private sealed class CapturingQueue : IMetadataQueue
    {
        public readonly List<(Guid Id, LibraryType Type, bool RefreshImages)> Calls = new();
        public Task EnqueueMetadataRefreshAsync(Guid mediaId, LibraryType type, bool refreshImages = true, int retryCount = 0, Guid? libraryId = null)
        {
            Calls.Add((mediaId, type, refreshImages));
            return Task.CompletedTask;
        }

        public int GetPendingCountForLibrary(Guid libraryId) => 0;
    }

    private ArtworkRepairService Build(CapturingQueue queue)
    {
        var env = new Mock<IWebHostEnvironment>();
        env.Setup(e => e.WebRootPath).Returns(_webRoot);
        return new ArtworkRepairService(
            new AppDbContext(_options), queue, env.Object, NullLogger<ArtworkRepairService>.Instance);
    }

    private string WriteCacheFile(string relativeUnderCache)
    {
        var full = Path.Combine(_webRoot, "cache", "images", relativeUnderCache.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, new byte[] { 1, 2, 3 });
        return "/cache/images/" + relativeUnderCache;
    }

    [Fact]
    public async Task Repair_ReEnqueuesOnlyMissing_HonoursLock_MapsEpisodeToSeries()
    {
        var movieLib = new Library { Id = Guid.NewGuid(), Name = "Movies", Type = LibraryType.Movie };
        var tvLib = new Library { Id = Guid.NewGuid(), Name = "TV", Type = LibraryType.TV };

        var missingMovie = new MediaItem { Id = Guid.NewGuid(), LibraryId = movieLib.Id, Type = MediaType.Movie, Title = "Missing", PosterUrl = "/cache/images/movies/missing_poster.jpg" };
        var presentPoster = WriteCacheFile("movies/present_poster.jpg");
        var presentMovie = new MediaItem { Id = Guid.NewGuid(), LibraryId = movieLib.Id, Type = MediaType.Movie, Title = "Present", PosterUrl = presentPoster };
        var externalMovie = new MediaItem { Id = Guid.NewGuid(), LibraryId = movieLib.Id, Type = MediaType.Movie, Title = "External", PosterUrl = "https://m.media-amazon.com/x.jpg" };
        var lockedMovie = new MediaItem { Id = Guid.NewGuid(), LibraryId = movieLib.Id, Type = MediaType.Movie, Title = "Locked", PosterUrl = "/cache/images/movies/locked_poster.jpg", MetadataLocked = true };

        var series = new MediaItem { Id = Guid.NewGuid(), LibraryId = tvLib.Id, Type = MediaType.Series, Title = "Show", PosterUrl = "/cache/images/tv/show_poster.jpg" };
        // Episode still missing -> parent series should be re-queued, not the episode.
        var episode = new MediaItem { Id = Guid.NewGuid(), LibraryId = tvLib.Id, Type = MediaType.Episode, Title = "Ep1", SeriesId = series.Id, BackdropUrl = "/cache/images/tv/show_s01e01_still.jpg" };

        using (var ctx = new AppDbContext(_options))
        {
            ctx.Libraries.AddRange(movieLib, tvLib);
            ctx.MediaItems.AddRange(missingMovie, presentMovie, externalMovie, lockedMovie, series, episode);
            await ctx.SaveChangesAsync();
        }

        var queue = new CapturingQueue();
        var result = await Build(queue).RepairAsync(CancellationToken.None);

        var ids = queue.Calls.Select(c => c.Id).ToHashSet();

        // Re-queued: the movie with a missing file, and the series (covering the episode still).
        Assert.Contains(missingMovie.Id, ids);
        Assert.Contains(series.Id, ids);
        // NOT re-queued: present file, external URL, locked item, or the episode itself.
        Assert.DoesNotContain(presentMovie.Id, ids);
        Assert.DoesNotContain(externalMovie.Id, ids);
        Assert.DoesNotContain(lockedMovie.Id, ids);
        Assert.DoesNotContain(episode.Id, ids);

        // Library type drives provider routing.
        Assert.Equal(LibraryType.Movie, queue.Calls.Single(c => c.Id == missingMovie.Id).Type);
        Assert.Equal(LibraryType.TV, queue.Calls.Single(c => c.Id == series.Id).Type);
        // Repair always refreshes images.
        Assert.All(queue.Calls, c => Assert.True(c.RefreshImages));

        Assert.Equal(2, result.ItemsReEnqueued);
        Assert.Equal(1, result.LockedSkipped);
        // missingMovie poster + lockedMovie poster + series poster + episode still = 4 missing refs.
        Assert.Equal(4, result.MissingImages);
    }

    [Fact]
    public async Task Repair_NoMissingFiles_EnqueuesNothing()
    {
        var lib = new Library { Id = Guid.NewGuid(), Name = "Movies", Type = LibraryType.Movie };
        var present = WriteCacheFile("movies/ok_poster.jpg");
        var movie = new MediaItem { Id = Guid.NewGuid(), LibraryId = lib.Id, Type = MediaType.Movie, Title = "OK", PosterUrl = present };
        using (var ctx = new AppDbContext(_options))
        {
            ctx.Libraries.Add(lib);
            ctx.MediaItems.Add(movie);
            await ctx.SaveChangesAsync();
        }

        var queue = new CapturingQueue();
        var result = await Build(queue).RepairAsync(CancellationToken.None);

        Assert.Empty(queue.Calls);
        Assert.Equal(0, result.MissingImages);
        Assert.Equal(0, result.ItemsReEnqueued);
    }

    [Fact]
    public async Task Repair_DetectsAbsolutePathCovers_LikeMusicScannerWrites()
    {
        // MusicScanner stores embedded album covers as an ABSOLUTE filesystem path
        // (under wwwroot/cache), not a /cache/ URL. After a DB-only restore that file
        // is gone, so the album must still be detected and re-queued.
        var lib = new Library { Id = Guid.NewGuid(), Name = "Music", Type = LibraryType.Music };

        var missingAbs = Path.Combine(_webRoot, "cache", "images", "music", "gone_cover.jpg"); // not created
        var missingAlbum = new MediaItem { Id = Guid.NewGuid(), LibraryId = lib.Id, Type = MediaType.Album, Title = "Gone", CoverArtPath = missingAbs };

        // A folder cover that lives in the media library survives a restore -> must NOT be flagged.
        var presentAbs = Path.Combine(_tempRoot, "library", "album", "folder.jpg");
        Directory.CreateDirectory(Path.GetDirectoryName(presentAbs)!);
        File.WriteAllBytes(presentAbs, new byte[] { 1 });
        var presentAlbum = new MediaItem { Id = Guid.NewGuid(), LibraryId = lib.Id, Type = MediaType.Album, Title = "Present", CoverArtPath = presentAbs };

        using (var ctx = new AppDbContext(_options))
        {
            ctx.Libraries.Add(lib);
            ctx.MediaItems.AddRange(missingAlbum, presentAlbum);
            await ctx.SaveChangesAsync();
        }

        var queue = new CapturingQueue();
        var result = await Build(queue).RepairAsync(CancellationToken.None);

        var ids = queue.Calls.Select(c => c.Id).ToHashSet();
        Assert.Contains(missingAlbum.Id, ids);
        Assert.DoesNotContain(presentAlbum.Id, ids);
        Assert.Equal(LibraryType.Music, queue.Calls.Single(c => c.Id == missingAlbum.Id).Type);
        Assert.Equal(1, result.MissingImages);
        Assert.Equal(0, result.FailedEnqueue);
    }

    [Fact]
    public async Task Repair_ComicIssue_CountsAsNeedsRescan_NotReEnqueued()
    {
        var lib = new Library { Id = Guid.NewGuid(), Name = "Comics", Type = LibraryType.Book };
        var issue = new MediaItem { Id = Guid.NewGuid(), LibraryId = lib.Id, Type = MediaType.ComicIssue, Title = "Issue 1", PosterUrl = "/cache/images/books/issue_poster.jpg" };
        using (var ctx = new AppDbContext(_options))
        {
            ctx.Libraries.Add(lib);
            ctx.MediaItems.Add(issue);
            await ctx.SaveChangesAsync();
        }

        var queue = new CapturingQueue();
        var result = await Build(queue).RepairAsync(CancellationToken.None);

        Assert.Empty(queue.Calls);
        Assert.Equal(1, result.NeedsRescan);
        Assert.Equal(1, result.MissingImages);
    }
}
