using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SoftMedia.Server.Data;
using SoftMedia.Server.Models;
using SoftMedia.Server.Services.Abstractions;
using SoftMedia.Server.Services.Background;
using SoftMedia.Server.Services.Infrastructure;
using SoftMedia.Server.Services.Media;
using Xunit;

namespace SoftMedia.Server.Tests.Services.Background;

/// SR-WI-037 — the daily orphaned-artwork sweep builds its valid-id set from the RAW
/// MediaItems DbSet (row-existence): soft-deleted (IsMissing) rows keep their cached
/// artwork so it heals when the drive returns; only ids with no row at all are orphans.
/// Uses real in-memory SQLite + a real ImageCacheService over a temp webroot.
public class ImageCacheCleanupServiceTests : IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly string _webRoot;
    private readonly string _cacheRoot;
    private readonly ServiceProvider _provider;
    private readonly ScheduledTaskRegistry _registry = new();
    private readonly ImageCacheCleanupService _worker;

    public ImageCacheCleanupServiceTests()
    {
        _conn = new SqliteConnection("DataSource=:memory:");
        _conn.Open();

        _webRoot = Path.Combine(Path.GetTempPath(), "sm-imgclean-" + Guid.NewGuid().ToString("N"), "wwwroot");
        Directory.CreateDirectory(_webRoot);
        _cacheRoot = Path.Combine(_webRoot, "cache", "images");

        var env = new Mock<IWebHostEnvironment>();
        env.Setup(e => e.WebRootPath).Returns(_webRoot);
        var imageCache = new ImageCacheService(new HttpClient(),
            NullLogger<ImageCacheService>.Instance, env.Object, Mock.Of<IStreamSecurityService>());

        // Real artifact stores over the same temp webroot — the sweep exercises their
        // cleanup paths only (no ffmpeg/Skia work is ever triggered by a sweep).
        var thumbnails = new ThumbnailService(env.Object,
            NullLogger<ThumbnailService>.Instance, Mock.Of<IBinaryLocationService>());
        var trickplay = new TrickplayService(env.Object, Mock.Of<IBinaryLocationService>(),
            Mock.Of<ISettingsService>(), NullLogger<TrickplayService>.Instance);
        var subtitles = new SubtitleService(NullLogger<SubtitleService>.Instance,
            Mock.Of<IProcessRunner>(), Mock.Of<IBinaryLocationService>(), env.Object);
        var proxyStore = new ProxyImageStore(env.Object, thumbnails,
            NullLogger<ProxyImageStore>.Instance);

        var services = new ServiceCollection();
        services.AddDbContext<AppDbContext>(o => o.UseSqlite(_conn));
        services.AddSingleton<IImageCacheService>(imageCache);
        services.AddSingleton<IThumbnailService>(thumbnails);
        services.AddSingleton<ITrickplayService>(trickplay);
        services.AddSingleton<ISubtitleService>(subtitles);
        services.AddSingleton<IProxyImageStore>(proxyStore);
        _provider = services.BuildServiceProvider();

        using (var scope = _provider.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.EnsureCreated();
        }

        _worker = new ImageCacheCleanupService(
            _provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<ImageCacheCleanupService>.Instance,
            _registry);
    }

    public void Dispose()
    {
        _provider.Dispose();
        _conn.Dispose();
        try { Directory.Delete(Directory.GetParent(_webRoot)!.FullName, true); } catch { }
    }

    private string Touch(string subDir, string fileName)
    {
        var path = Path.Combine(_cacheRoot, subDir, fileName);
        File.WriteAllBytes(path, new byte[] { 1, 2, 3 });
        return path;
    }

    private (Guid liveId, Guid missingId) SeedRows()
    {
        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var lib = new Library { Id = Guid.NewGuid(), Name = "Movies", Type = LibraryType.Movie, Paths = new() { "/m" } };
        db.Libraries.Add(lib);

        var live = new MediaItem
        {
            Id = Guid.NewGuid(), LibraryId = lib.Id, Type = MediaType.Movie,
            Title = "Live", Path = "/m/live.mkv",
        };
        var missing = new MediaItem
        {
            Id = Guid.NewGuid(), LibraryId = lib.Id, Type = MediaType.Movie,
            Title = "Offline", Path = "/m/offline.mkv",
            IsMissing = true, // soft-deleted: the row exists, art must be retained
        };
        db.MediaItems.AddRange(live, missing);
        db.SaveChanges();
        return (live.Id, missing.Id);
    }

    [Fact]
    public async Task RunOnce_DeletesOrphanArt_RetainsLiveAndIsMissingArt()
    {
        var (liveId, missingId) = SeedRows();
        var orphanId = Guid.NewGuid(); // no DB row at all

        var liveFile = Touch("movies", $"{liveId}_poster.jpg");
        var missingFile = Touch("movies", $"{missingId}_poster.jpg");
        var orphanFile = Touch("movies", $"{orphanId}_poster.jpg");
        var orphanBook = Touch("books", $"{orphanId}_poster.jpg"); // books dir is swept too

        var deleted = await _worker.RunOnceAsync();

        Assert.Equal(2, deleted);
        Assert.True(File.Exists(liveFile), "art for a live row must be retained");
        Assert.True(File.Exists(missingFile), "art for an IsMissing (soft-deleted) row must be retained");
        Assert.False(File.Exists(orphanFile), "art for an id with no DB row must be deleted");
        Assert.False(File.Exists(orphanBook), "book covers with no DB row must be deleted");
    }

    [Fact]
    public async Task RunOnce_EmptyDatabase_SweepsEverything()
    {
        var a = Touch("movies", $"{Guid.NewGuid()}_poster.jpg");
        var b = Touch("books", $"{Guid.NewGuid()}_poster.jpg");

        var deleted = await _worker.RunOnceAsync();

        Assert.Equal(2, deleted);
        Assert.False(File.Exists(a));
        Assert.False(File.Exists(b));
    }

    /// A row left pointing at the provider while its poster is already cached on disk is
    /// re-fetched through /api/v1/image/proxy on every library view, and nothing else ever
    /// rewrites the column (the item looks complete to MetadataEnrichmentPolicy). The sweep
    /// adopts the cached file instead.
    [Fact]
    public async Task RunOnce_PointsRemotePosterRowsAtArtworkAlreadyCachedOnDisk()
    {
        var (liveId, _) = SeedRows();
        Touch("movies", $"{liveId}_poster.jpg");

        using (var scope = _provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var item = await db.MediaItems.FirstAsync(m => m.Id == liveId);
            item.PosterUrl = "https://m.media-amazon.com/images/M/poster.jpg";
            db.LibraryRecentCaches.Add(new LibraryRecentCache
            {
                LibraryId = item.LibraryId,
                CachedJson = "[]",
                LastUpdated = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        await _worker.RunOnceAsync();

        using (var scope = _provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var item = await db.MediaItems.FirstAsync(m => m.Id == liveId);
            Assert.Equal($"/cache/images/movies/{liveId}_poster.jpg", item.PosterUrl);
            Assert.Empty(await db.LibraryRecentCaches.ToListAsync());
        }
    }

    /// Sidecar copies ("_poster_local") and season posters are owned by other columns/flags —
    /// adopting them under the item's own PosterUrl would mis-attribute the art.
    [Fact]
    public async Task RunOnce_DoesNotAdoptSidecarOrSeasonKeys()
    {
        var (liveId, _) = SeedRows();
        Touch("movies", $"{liveId}_poster_local.jpg");
        Touch("tv", $"{liveId}_season01_poster.jpg");

        const string remote = "https://m.media-amazon.com/images/M/poster.jpg";
        using (var scope = _provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            (await db.MediaItems.FirstAsync(m => m.Id == liveId)).PosterUrl = remote;
            await db.SaveChangesAsync();
        }

        await _worker.RunOnceAsync();

        using (var scope = _provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.Equal(remote, (await db.MediaItems.FirstAsync(m => m.Id == liveId)).PosterUrl);
        }
    }

    /// Trickplay sheets follow the same row-existence contract as artwork: rows (even
    /// IsMissing ones) keep their directory; guids with no row are swept.
    [Fact]
    public async Task RunOnce_SweepsOrphanTrickplay_RetainsLiveAndIsMissing()
    {
        var (liveId, missingId) = SeedRows();
        var orphanId = Guid.NewGuid();

        string TrickplayDir(Guid id)
        {
            var dir = Path.Combine(_webRoot, "cache", "trickplay", id.ToString("N"));
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "manifest.json"), "{}");
            return dir;
        }
        var liveDir = TrickplayDir(liveId);
        var missingDir = TrickplayDir(missingId);
        var orphanDir = TrickplayDir(orphanId);

        await _worker.RunOnceAsync();

        Assert.True(Directory.Exists(liveDir));
        Assert.True(Directory.Exists(missingDir), "IsMissing rows keep trickplay so it heals when the drive returns");
        Assert.False(Directory.Exists(orphanDir));
    }

    /// Cast headshots are keyed by Person.ExternalId; valid = referenced by any cast row.
    [Fact]
    public async Task RunOnce_SweepsCastImagesWithNoReferencingCastRow()
    {
        var (liveId, _) = SeedRows();
        using (var scope = _provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var person = new Person { Name = "Kept Actor", ExternalId = 777 };
            db.Persons.Add(person);
            await db.SaveChangesAsync();
            db.MediaItemCasts.Add(new MediaItemCast { MediaItemId = liveId, PersonId = person.Id });
            // An orphaned Person row with no cast rows — their file must be swept even
            // though the row survives (Person rows are global and never deleted).
            db.Persons.Add(new Person { Name = "Orphan Actor", ExternalId = 888 });
            await db.SaveChangesAsync();
        }

        var castDir = Path.Combine(_cacheRoot, "tv", "cast");
        var kept = Path.Combine(castDir, "777.jpg");
        var orphanByPerson = Path.Combine(castDir, "888.jpg");
        var orphanNoRow = Path.Combine(castDir, "999.jpg");
        foreach (var f in new[] { kept, orphanByPerson, orphanNoRow }) File.WriteAllBytes(f, new byte[] { 1 });

        await _worker.RunOnceAsync();

        Assert.True(File.Exists(kept), "headshot of a person still referenced by a cast row must be retained");
        Assert.False(File.Exists(orphanByPerson));
        Assert.False(File.Exists(orphanNoRow));

        // MC-WI-005: the uncredited Person ROW is also removed (previously nothing ever
        // deleted Persons); the credited one survives.
        using (var scope = _provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var names = await db.Persons.Select(p => p.Name).ToListAsync();
            Assert.Contains("Kept Actor", names);
            Assert.DoesNotContain("Orphan Actor", names);
        }
    }

    /// MC-WI-006 — Genre rows with no MediaItemGenre link are unreachable in the UI and
    /// are reaped by the sweep; linked genres survive.
    [Fact]
    public async Task RunOnce_RemovesGenresWithNoLinks()
    {
        var (liveId, _) = SeedRows();
        using (var scope = _provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var linked = new Genre { Name = "Drama" };
            var orphan = new Genre { Name = "Ghost Genre" };
            db.Genres.AddRange(linked, orphan);
            await db.SaveChangesAsync();
            db.MediaItemGenres.Add(new MediaItemGenre { MediaItemId = liveId, GenreId = linked.Id });
            await db.SaveChangesAsync();
        }

        await _worker.RunOnceAsync();

        using (var scope = _provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var names = await db.Genres.Select(g => g.Name).ToListAsync();
            Assert.Contains("Drama", names);
            Assert.DoesNotContain("Ghost Genre", names);
        }
    }

    /// Cached subtitle extractions are keyed by a hash of the source path; a hash matching
    /// no row's path (including IsMissing rows' paths) is an orphan.
    [Fact]
    public async Task RunOnce_SweepsSubtitleVttForUnknownSourcePaths()
    {
        SeedRows(); // rows have paths /m/live.mkv and /m/offline.mkv

        static string HashPath(string path)
        {
            var canonical = Path.GetFullPath(path).ToLowerInvariant();
            var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(canonical));
            return Convert.ToHexString(hash, 0, 8).ToLowerInvariant();
        }

        var subsDir = Path.Combine(_webRoot, "cache", "subtitles");
        Directory.CreateDirectory(subsDir);
        var liveVtt = Path.Combine(subsDir, $"{HashPath("/m/live.mkv")}_s0_123.vtt");
        var missingVtt = Path.Combine(subsDir, $"{HashPath("/m/offline.mkv")}_s1_456.vtt");
        var orphanVtt = Path.Combine(subsDir, $"{HashPath("/gone/deleted.mkv")}_s0_789.vtt");
        foreach (var f in new[] { liveVtt, missingVtt, orphanVtt }) File.WriteAllText(f, "WEBVTT");

        await _worker.RunOnceAsync();

        Assert.True(File.Exists(liveVtt));
        Assert.True(File.Exists(missingVtt), "IsMissing rows keep their extractions so playback heals with the drive");
        Assert.False(File.Exists(orphanVtt));
    }

    /// Proxy copies expire by age only (their hash keys are uncorrelatable with items);
    /// a cache hit refreshes mtime, so fresh files always survive.
    [Fact]
    public async Task RunOnce_ExpiresOldProxyCopies_KeepsFresh()
    {
        SeedRows();
        var proxyDir = Path.Combine(_cacheRoot, "proxy");
        Directory.CreateDirectory(proxyDir);
        var oldFile = Path.Combine(proxyDir, new string('a', 64) + ".jpg");
        var oldSentinel = Path.Combine(proxyDir, new string('b', 64) + ".jpg.404");
        var freshFile = Path.Combine(proxyDir, new string('c', 64) + ".jpg");
        foreach (var f in new[] { oldFile, oldSentinel, freshFile }) File.WriteAllBytes(f, new byte[] { 1 });
        File.SetLastWriteTimeUtc(oldFile, DateTime.UtcNow.AddDays(-40));
        File.SetLastWriteTimeUtc(oldSentinel, DateTime.UtcNow.AddDays(-40));

        await _worker.RunOnceAsync();

        Assert.False(File.Exists(oldFile));
        Assert.False(File.Exists(oldSentinel));
        Assert.True(File.Exists(freshFile));
    }

    /// Thumbnails mix media-item keys with proxy-derived keys, so unknown keys are only
    /// reaped once stale; item-keyed thumbnails follow row-existence regardless of age.
    [Fact]
    public async Task RunOnce_SweepsStaleOrphanThumbnails_KeepsItemKeyedAndFresh()
    {
        var (liveId, _) = SeedRows();
        var thumbsDir = Path.Combine(_cacheRoot, "thumbnails");
        Directory.CreateDirectory(thumbsDir);
        var liveThumb = Path.Combine(thumbsDir, $"{liveId}_320.webp");
        var staleOrphan = Path.Combine(thumbsDir, $"{Guid.NewGuid()}_320.webp");
        var freshOrphan = Path.Combine(thumbsDir, $"{Guid.NewGuid()}_320.webp");
        foreach (var f in new[] { liveThumb, staleOrphan, freshOrphan }) File.WriteAllBytes(f, new byte[] { 1 });
        File.SetLastWriteTimeUtc(liveThumb, DateTime.UtcNow.AddDays(-30));
        File.SetLastWriteTimeUtc(staleOrphan, DateTime.UtcNow.AddDays(-30));

        await _worker.RunOnceAsync();

        Assert.True(File.Exists(liveThumb), "an item-keyed thumbnail is retained while its row exists");
        Assert.False(File.Exists(staleOrphan));
        Assert.True(File.Exists(freshOrphan), "an unknown key younger than the min-age must survive (may be an active proxy thumbnail)");
    }

    /// When the adoption pass repoints a row from its provider URL to the cached file, the
    /// proxy's copy of that URL must be deleted in the same pass — the URL being
    /// overwritten is the only surviving key to the hash-named file.
    [Fact]
    public async Task RunOnce_AdoptionDeletesTheProxyCopyOfTheOldUrl()
    {
        var (liveId, _) = SeedRows();
        Touch("movies", $"{liveId}_poster.jpg");

        const string remote = "https://m.media-amazon.com/images/M/poster.jpg";
        using (var scope = _provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            (await db.MediaItems.FirstAsync(m => m.Id == liveId)).PosterUrl = remote;
            await db.SaveChangesAsync();
        }

        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(remote)));
        var proxyDir = Path.Combine(_cacheRoot, "proxy");
        Directory.CreateDirectory(proxyDir);
        var proxyCopy = Path.Combine(proxyDir, hash + ".jpg");
        File.WriteAllBytes(proxyCopy, new byte[] { 1 });

        await _worker.RunOnceAsync();

        using (var scope = _provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.Equal($"/cache/images/movies/{liveId}_poster.jpg",
                (await db.MediaItems.FirstAsync(m => m.Id == liveId)).PosterUrl);
        }
        Assert.False(File.Exists(proxyCopy), "the adopted row's proxy copy must be deleted, not orphaned");
    }

    [Fact]
    public void Constructor_RegistersAdminVisibleManuallyTriggerableTask()
    {
        var status = Assert.Single(_registry.GetAll(),
            t => t.Name == ImageCacheCleanupService.RegisteredTaskName);
        Assert.Equal(TaskSchedule.Scheduled, status.Schedule);
        Assert.True(status.SupportsManualTrigger);
        Assert.Equal(ImageCacheCleanupService.RegisteredTaskName, _worker.TaskName);
    }

    [Fact]
    public async Task TriggerNow_RunsASweep_AndReportsToRegistry()
    {
        SeedRows();
        var orphan = Touch("movies", $"{Guid.NewGuid()}_poster.jpg");

        // Anchor on LastRunUtc changing (not LastResult being set): persisted telemetry from a
        // previous process could pre-populate LastResult via the ctor's best-effort restore.
        ScheduledTaskStatus Status() =>
            _registry.GetAll().Single(t => t.Name == ImageCacheCleanupService.RegisteredTaskName);
        var before = Status().LastRunUtc;

        _worker.TriggerNow();

        // Fire-and-forget: poll the registry until the background run has reported.
        for (var i = 0; i < 100 && Status().LastRunUtc == before; i++)
        {
            await Task.Delay(50);
        }

        Assert.Equal("Success", Status().LastResult);
        Assert.False(File.Exists(orphan));
    }
}
