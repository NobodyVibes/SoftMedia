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

        var services = new ServiceCollection();
        services.AddDbContext<AppDbContext>(o => o.UseSqlite(_conn));
        services.AddSingleton<IImageCacheService>(imageCache);
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
