using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Moq;
using SoftMedia.Server.Data;
using SoftMedia.Server.Models;
using SoftMedia.Server.Services.Infrastructure;
using SoftMedia.Server.Services.Security.ContentRating;
using SoftMedia.Server.Services.Security.LibraryAccess;
using Xunit;

namespace SoftMedia.Server.Tests.Services.Infrastructure;

/// Wave C — verifies MediaRepository honours per-user ACL on its read APIs.
public class MediaRepositoryAclTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;
    private readonly Library _libA;
    private readonly Library _libB;
    private readonly MediaItem _itemA;
    private readonly MediaItem _itemB;
    private readonly MediaItem _seriesA;
    private readonly MediaItem _episodeA;

    public MediaRepositoryAclTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options;

        _libA = new Library { Id = Guid.NewGuid(), Name = "A", Type = LibraryType.Movie, Paths = new() { "/a" } };
        _libB = new Library { Id = Guid.NewGuid(), Name = "B", Type = LibraryType.Movie, Paths = new() { "/b" } };

        _itemA = new MediaItem { Id = Guid.NewGuid(), LibraryId = _libA.Id, Title = "A1", SortTitle = "A1", Path = "/a/1", Type = MediaType.Movie };
        _itemB = new MediaItem { Id = Guid.NewGuid(), LibraryId = _libB.Id, Title = "B1", SortTitle = "B1", Path = "/b/1", Type = MediaType.Movie };
        _seriesA = new MediaItem { Id = Guid.NewGuid(), LibraryId = _libA.Id, Title = "Show", SortTitle = "Show", Path = "/a/show", Type = MediaType.Series };
        _episodeA = new MediaItem
        {
            Id = Guid.NewGuid(), LibraryId = _libA.Id, Title = "S01E01", SortTitle = "S01E01",
            Path = "/a/show/s01e01", Type = MediaType.Episode,
            SeriesId = _seriesA.Id, SeasonNumber = 1, EpisodeNumber = 1,
            DateAdded = DateTime.UtcNow,
        };

        using var ctx = new AppDbContext(_options);
        ctx.Database.EnsureCreated();
        ctx.Libraries.AddRange(_libA, _libB);
        ctx.MediaItems.AddRange(_itemA, _itemB, _seriesA, _episodeA);
        ctx.SaveChanges();
    }

    public void Dispose() => _connection.Dispose();

    private MediaRepository BuildRepo(LibraryAccess access, AppDbContext db)
    {
        var rating = new Mock<IUserContentRatingProvider>();
        rating.Setup(r => r.GetCurrentAsync()).ReturnsAsync(UserRatingCeilings.Unrestricted);

        var libraryAccess = new Mock<IUserLibraryAccessProvider>();
        libraryAccess.Setup(p => p.GetCurrentAsync()).ReturnsAsync(access);

        return new MediaRepository(db, rating.Object, libraryAccess.Object);
    }

    [Fact]
    public async Task GetByIdAsync_BlockedLibrary_ReturnsNull()
    {
        using var db = new AppDbContext(_options);
        var repo = BuildRepo(LibraryAccess.AllowOnly(new[] { _libA.Id }), db);

        Assert.Null(await repo.GetByIdAsync(_itemB.Id));
        Assert.NotNull(await repo.GetByIdAsync(_itemA.Id));
    }

    [Fact]
    public async Task GetByIdWithLibraryAsync_BlockedLibrary_ReturnsNull()
    {
        using var db = new AppDbContext(_options);
        var repo = BuildRepo(LibraryAccess.AllowOnly(new[] { _libA.Id }), db);

        Assert.Null(await repo.GetByIdWithLibraryAsync(_itemB.Id));
        Assert.NotNull(await repo.GetByIdWithLibraryAsync(_itemA.Id));
    }

    [Fact]
    public async Task GetRecentMediaAsync_StripsBlockedLibraryItems()
    {
        using var db = new AppDbContext(_options);
        var repo = BuildRepo(LibraryAccess.AllowOnly(new[] { _libA.Id }), db);

        var recent = (await repo.GetRecentMediaAsync(10, type: null)).ToList();

        Assert.DoesNotContain(recent, m => m.LibraryId == _libB.Id);
        Assert.Contains(recent, m => m.Id == _itemA.Id);
    }

    [Fact]
    public async Task GetEpisodesAsync_BlockedLibrary_ReturnsEmpty()
    {
        using var db = new AppDbContext(_options);
        var repo = BuildRepo(LibraryAccess.AllowOnly(new[] { _libB.Id }), db);

        var episodes = (await repo.GetEpisodesAsync(_seriesA.Id)).ToList();

        Assert.Empty(episodes);
    }

    [Fact]
    public async Task GetEpisodesAsync_AllowedLibrary_ReturnsEpisodes()
    {
        using var db = new AppDbContext(_options);
        var repo = BuildRepo(LibraryAccess.AllowOnly(new[] { _libA.Id }), db);

        var episodes = (await repo.GetEpisodesAsync(_seriesA.Id)).ToList();

        Assert.Single(episodes);
        Assert.Equal(_episodeA.Id, episodes[0].Id);
    }

    [Fact]
    public async Task ExistsAsync_BlockedLibrary_ReturnsFalse()
    {
        using var db = new AppDbContext(_options);
        var repo = BuildRepo(LibraryAccess.AllowOnly(new[] { _libA.Id }), db);

        Assert.False(await repo.ExistsAsync(_itemB.Id));
        Assert.True(await repo.ExistsAsync(_itemA.Id));
    }

    [Fact]
    public async Task Unrestricted_AllItemsVisible()
    {
        using var db = new AppDbContext(_options);
        var repo = BuildRepo(LibraryAccess.Unrestricted, db);

        Assert.NotNull(await repo.GetByIdAsync(_itemA.Id));
        Assert.NotNull(await repo.GetByIdAsync(_itemB.Id));
        var recent = (await repo.GetRecentMediaAsync(10, type: null)).ToList();
        Assert.Contains(recent, m => m.LibraryId == _libA.Id);
        Assert.Contains(recent, m => m.LibraryId == _libB.Id);
    }
}
