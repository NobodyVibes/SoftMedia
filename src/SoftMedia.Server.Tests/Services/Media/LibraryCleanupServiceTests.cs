using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SoftMedia.Server.Data;
using SoftMedia.Server.Models;
using SoftMedia.Server.Services.Abstractions;
using SoftMedia.Server.Services.Infrastructure;
using SoftMedia.Server.Services.Media;
using Xunit;

namespace SoftMedia.Server.Tests.Services.Media;

/// Library deletion must remove every derived on-disk artifact for the library's items:
/// artwork, trickplay sheets, thumbnails, cached subtitle extractions — and cast
/// headshots, which are keyed by Person.ExternalId (NOT the Person PK; passing PKs
/// silently deleted nothing) and shared globally, so a person also credited in another
/// library keeps their image.
public class LibraryCleanupServiceTests : IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly AppDbContext _db;
    private readonly string _webRoot;
    private readonly string _cacheRoot;
    private readonly LibraryCleanupService _svc;
    private readonly TrickplayService _trickplay;

    public LibraryCleanupServiceTests()
    {
        _conn = new SqliteConnection("DataSource=:memory:");
        _conn.Open();
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_conn).Options);
        _db.Database.EnsureCreated();

        _webRoot = Path.Combine(Path.GetTempPath(), "sm-libclean-" + Guid.NewGuid().ToString("N"), "wwwroot");
        Directory.CreateDirectory(_webRoot);
        _cacheRoot = Path.Combine(_webRoot, "cache", "images");

        var env = new Mock<IWebHostEnvironment>();
        env.Setup(e => e.WebRootPath).Returns(_webRoot);

        var imageCache = new ImageCacheService(new HttpClient(),
            NullLogger<ImageCacheService>.Instance, env.Object, Mock.Of<IStreamSecurityService>());
        _trickplay = new TrickplayService(env.Object, Mock.Of<IBinaryLocationService>(),
            Mock.Of<ISettingsService>(), NullLogger<TrickplayService>.Instance);
        var thumbnails = new ThumbnailService(env.Object,
            NullLogger<ThumbnailService>.Instance, Mock.Of<IBinaryLocationService>());
        var subtitles = new SubtitleService(NullLogger<SubtitleService>.Instance,
            Mock.Of<IProcessRunner>(), Mock.Of<IBinaryLocationService>(), env.Object);

        _svc = new LibraryCleanupService(_db, imageCache, _trickplay, thumbnails, subtitles,
            NullLogger<LibraryCleanupService>.Instance);
    }

    public void Dispose()
    {
        _db.Dispose();
        _conn.Dispose();
        try { Directory.Delete(Directory.GetParent(_webRoot)!.FullName, true); } catch { }
    }

    private (Library lib, MediaItem item) SeedLibraryWithItem(string name, string itemPath)
    {
        var lib = new Library { Id = Guid.NewGuid(), Name = name, Type = LibraryType.TV, Paths = new() { "/" + name } };
        var item = new MediaItem
        {
            Id = Guid.NewGuid(), LibraryId = lib.Id, Type = MediaType.Movie,
            Title = name + " item", Path = itemPath,
        };
        _db.Libraries.Add(lib);
        _db.MediaItems.Add(item);
        _db.SaveChanges();
        return (lib, item);
    }

    private Person SeedPersonWithCast(Guid mediaItemId, int externalId)
    {
        var person = new Person { Name = "Person " + externalId, ExternalId = externalId };
        _db.Persons.Add(person);
        _db.SaveChanges();
        _db.MediaItemCasts.Add(new MediaItemCast { MediaItemId = mediaItemId, PersonId = person.Id });
        _db.SaveChanges();
        return person;
    }

    private string Touch(params string[] relative)
    {
        var path = Path.Combine(new[] { _webRoot }.Concat(relative).ToArray());
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[] { 1, 2, 3 });
        return path;
    }

    [Fact]
    public async Task Delete_RemovesExclusiveCastImage_KeepsShared()
    {
        var (libA, itemA) = SeedLibraryWithItem("A", "/A/a.mkv");
        var (_, itemB) = SeedLibraryWithItem("B", "/B/b.mkv");

        var exclusive = SeedPersonWithCast(itemA.Id, 1001);
        var shared = SeedPersonWithCast(itemA.Id, 2002);
        _db.MediaItemCasts.Add(new MediaItemCast { MediaItemId = itemB.Id, PersonId = shared.Id });
        _db.SaveChanges();

        // Files are keyed by ExternalId — the whole point of the fix. Also plant a file
        // named after the exclusive person's PK: it must NOT be deleted (it belongs to
        // whatever other person has that external id).
        var exclusiveFile = Touch("cache", "images", "tv", "cast", "1001.jpg");
        var sharedFile = Touch("cache", "images", "tv", "cast", "2002.jpg");
        var pkNamedFile = Touch("cache", "images", "tv", "cast", $"{exclusive.Id}0000.jpg");

        await _svc.DeleteArtifactsForLibraryAsync(libA.Id, new[] { (itemA.Id, itemA.Type) });

        Assert.False(File.Exists(exclusiveFile), "a person credited only in the deleted library loses their headshot");
        Assert.True(File.Exists(sharedFile), "a person also credited in another library keeps their headshot");
        Assert.True(File.Exists(pkNamedFile), "deletion is keyed strictly by external id, never the Person PK");
    }

    [Fact]
    public async Task Delete_RemovesArtworkTrickplayThumbnailsAndSubtitles()
    {
        var (lib, item) = SeedLibraryWithItem("C", "/C/movie.mkv");

        var poster = Touch("cache", "images", "movies", $"{item.Id}_poster.jpg");
        var thumb = Touch("cache", "images", "thumbnails", $"{item.Id}_320.webp");

        var trickplayDir = Path.Combine(_webRoot, "cache", "trickplay", item.Id.ToString("N"));
        Directory.CreateDirectory(trickplayDir);
        File.WriteAllText(Path.Combine(trickplayDir, "manifest.json"), "{}");

        // Same hash the subtitle cache uses: SHA256 over the lowercased full path, 8 bytes.
        var canonical = Path.GetFullPath("/C/movie.mkv").ToLowerInvariant();
        var prefix = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(canonical)), 0, 8).ToLowerInvariant();
        var vtt = Touch("cache", "subtitles", $"{prefix}_s0_123.vtt");

        await _svc.DeleteArtifactsForLibraryAsync(lib.Id, new[] { (item.Id, item.Type) });

        Assert.False(File.Exists(poster));
        Assert.False(File.Exists(thumb));
        Assert.False(Directory.Exists(trickplayDir));
        Assert.False(File.Exists(vtt));
        Assert.False(_trickplay.HasTrickplay(item.Id));
    }

    [Fact]
    public async Task Delete_LeavesOtherLibrariesArtifactsAlone()
    {
        var (libA, itemA) = SeedLibraryWithItem("D", "/D/a.mkv");
        var (_, itemB) = SeedLibraryWithItem("E", "/E/b.mkv");

        var posterB = Touch("cache", "images", "movies", $"{itemB.Id}_poster.jpg");
        var canonicalB = Path.GetFullPath("/E/b.mkv").ToLowerInvariant();
        var prefixB = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(canonicalB)), 0, 8).ToLowerInvariant();
        var vttB = Touch("cache", "subtitles", $"{prefixB}_s0_9.vtt");

        await _svc.DeleteArtifactsForLibraryAsync(libA.Id, new[] { (itemA.Id, itemA.Type) });

        Assert.True(File.Exists(posterB));
        Assert.True(File.Exists(vttB));
    }
}
