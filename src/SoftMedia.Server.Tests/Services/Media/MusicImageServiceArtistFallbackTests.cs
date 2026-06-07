using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SoftMedia.Server.Data;
using SoftMedia.Server.Models;
using SoftMedia.Server.Services.Media;
using Xunit;

namespace SoftMedia.Server.Tests.Services.Media;

/// The artist image falls back to an album cover when the artist has none of its
/// own. Regression: Anthrax stayed imageless because its EARLIEST album ("The Neil
/// Turbin Demos", 1982) had no cover, and the fallback blindly took the earliest
/// album's CoverArtPath instead of the earliest album that actually has one.
public class MusicImageServiceArtistFallbackTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;
    private readonly Guid _libraryId = Guid.NewGuid();

    public MusicImageServiceArtistFallbackTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;
        using var ctx = new AppDbContext(_options);
        ctx.Database.EnsureCreated();
        // A real Library row — MediaItem.LibraryId is a FK and SQLite enforces it.
        ctx.Libraries.Add(new Library { Id = _libraryId, Name = "Music", Type = LibraryType.Music, Paths = new() { "/m" } });
        ctx.SaveChanges();
    }

    public void Dispose() => _connection.Dispose();

    private MusicImageService Build(AppDbContext db)
    {
        var env = new Mock<IWebHostEnvironment>();
        env.Setup(e => e.WebRootPath).Returns("/tmp/wwwroot");
        return new MusicImageService(db, env.Object, NullLogger<MusicImageService>.Instance);
    }

    private MediaItem Album(Guid artistId, int year, string? cover) => new()
    {
        Id = Guid.NewGuid(),
        LibraryId = _libraryId,
        Title = $"Album {year}",
        SortTitle = $"Album {year}",
        Path = $"/m/{year}",
        Type = MediaType.Album,
        ArtistId = artistId,
        Year = year,
        CoverArtPath = cover,
    };

    [Fact]
    public async Task GetArtistImagePathAsync_SkipsEarliestCoverlessAlbum()
    {
        var artistId = Guid.NewGuid();
        using (var seed = new AppDbContext(_options))
        {
            seed.MediaItems.Add(new MediaItem
            {
                Id = artistId, LibraryId = _libraryId, Title = "Anthrax",
                SortTitle = "Anthrax", Path = "/m/anthrax", Type = MediaType.Artist,
                CoverArtPath = null,
            });
            seed.MediaItems.Add(Album(artistId, 1982, null));                                  // demos — no cover
            seed.MediaItems.Add(Album(artistId, 1984, "/cache/images/music/fistful_cover.jpg")); // first with art
            seed.MediaItems.Add(Album(artistId, 1985, "/cache/images/music/spreading_cover.jpg"));
            await seed.SaveChangesAsync();
        }

        using var db = new AppDbContext(_options);
        var result = await Build(db).GetArtistImagePathAsync(artistId);

        Assert.Equal("/cache/images/music/fistful_cover.jpg", result);
    }

    [Fact]
    public async Task GetArtistImagePathAsync_PrefersArtistsOwnCover()
    {
        var artistId = Guid.NewGuid();
        using (var seed = new AppDbContext(_options))
        {
            seed.MediaItems.Add(new MediaItem
            {
                Id = artistId, LibraryId = _libraryId, Title = "Anthrax",
                SortTitle = "Anthrax", Path = "/m/anthrax", Type = MediaType.Artist,
                CoverArtPath = "/cache/images/music/artist_anthrax.jpg",
            });
            seed.MediaItems.Add(Album(artistId, 1984, "/cache/images/music/fistful_cover.jpg"));
            await seed.SaveChangesAsync();
        }

        using var db = new AppDbContext(_options);
        var result = await Build(db).GetArtistImagePathAsync(artistId);

        Assert.Equal("/cache/images/music/artist_anthrax.jpg", result);
    }

    [Fact]
    public async Task GetArtistImagePathAsync_ReturnsNullWhenNoAlbumHasCover()
    {
        var artistId = Guid.NewGuid();
        using (var seed = new AppDbContext(_options))
        {
            seed.MediaItems.Add(new MediaItem
            {
                Id = artistId, LibraryId = _libraryId, Title = "Obscure",
                SortTitle = "Obscure", Path = "/m/obscure", Type = MediaType.Artist,
            });
            seed.MediaItems.Add(Album(artistId, 1990, null));
            await seed.SaveChangesAsync();
        }

        using var db = new AppDbContext(_options);
        var result = await Build(db).GetArtistImagePathAsync(artistId);

        Assert.Null(result);
    }
}
