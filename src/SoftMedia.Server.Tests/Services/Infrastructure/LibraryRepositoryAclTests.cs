using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Moq;
using SoftMedia.Server.Data;
using SoftMedia.Server.DTOs;
using SoftMedia.Server.Models;
using SoftMedia.Server.Services.Infrastructure;
using SoftMedia.Server.Services.Security.ContentRating;
using SoftMedia.Server.Services.Security.LibraryAccess;
using Xunit;

namespace SoftMedia.Server.Tests.Services.Infrastructure;

/// Wave C — verifies LibraryRepository honours per-user ACL: GetAllAsync,
/// GetByIdAsync, ExistsAsync return only allowed libraries; GetLibraryItemsAsync
/// returns an empty page for blocked libraries; admin-equivalent (Unrestricted)
/// short-circuits.
public class LibraryRepositoryAclTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;
    private readonly Library _libA;
    private readonly Library _libB;

    public LibraryRepositoryAclTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options;

        _libA = new Library { Id = Guid.NewGuid(), Name = "A", Type = LibraryType.Movie, Order = 0, Paths = new() { "/a" } };
        _libB = new Library { Id = Guid.NewGuid(), Name = "B", Type = LibraryType.Movie, Order = 1, Paths = new() { "/b" } };

        using var ctx = new AppDbContext(_options);
        ctx.Database.EnsureCreated();
        ctx.Libraries.AddRange(_libA, _libB);

        ctx.MediaItems.AddRange(
            new MediaItem { Id = Guid.NewGuid(), LibraryId = _libA.Id, Title = "A1", SortTitle = "A1", Path = "/a/1", Type = MediaType.Movie },
            new MediaItem { Id = Guid.NewGuid(), LibraryId = _libA.Id, Title = "A2", SortTitle = "A2", Path = "/a/2", Type = MediaType.Movie },
            new MediaItem { Id = Guid.NewGuid(), LibraryId = _libB.Id, Title = "B1", SortTitle = "B1", Path = "/b/1", Type = MediaType.Movie });

        ctx.SaveChanges();
    }

    public void Dispose() => _connection.Dispose();

    private LibraryRepository BuildRepo(LibraryAccess access, AppDbContext db)
    {
        var rating = new Mock<IUserContentRatingProvider>();
        rating.Setup(r => r.GetCurrentAsync()).ReturnsAsync(UserRatingCeilings.Unrestricted);

        var libraryAccess = new Mock<IUserLibraryAccessProvider>();
        libraryAccess.Setup(p => p.GetCurrentAsync()).ReturnsAsync(access);

        return new LibraryRepository(db, rating.Object, libraryAccess.Object);
    }

    [Fact]
    public async Task GetAllAsync_Unrestricted_ReturnsEverything()
    {
        using var db = new AppDbContext(_options);
        var repo = BuildRepo(LibraryAccess.Unrestricted, db);

        var libs = (await repo.GetAllAsync()).ToList();

        Assert.Equal(2, libs.Count);
    }

    [Fact]
    public async Task GetAllAsync_Restricted_ReturnsOnlyAllowed()
    {
        using var db = new AppDbContext(_options);
        var repo = BuildRepo(LibraryAccess.AllowOnly(new[] { _libA.Id }), db);

        var libs = (await repo.GetAllAsync()).ToList();

        Assert.Single(libs);
        Assert.Equal(_libA.Id, libs[0].Id);
    }

    [Fact]
    public async Task GetByIdAsync_BlockedLibrary_ReturnsNull()
    {
        using var db = new AppDbContext(_options);
        var repo = BuildRepo(LibraryAccess.AllowOnly(new[] { _libA.Id }), db);

        var blocked = await repo.GetByIdAsync(_libB.Id);

        Assert.Null(blocked);
    }

    [Fact]
    public async Task GetByIdAsync_AllowedLibrary_ReturnsRow()
    {
        using var db = new AppDbContext(_options);
        var repo = BuildRepo(LibraryAccess.AllowOnly(new[] { _libA.Id }), db);

        var row = await repo.GetByIdAsync(_libA.Id);

        Assert.NotNull(row);
        Assert.Equal(_libA.Id, row!.Id);
    }

    [Fact]
    public async Task ExistsAsync_BlockedLibrary_ReturnsFalse()
    {
        using var db = new AppDbContext(_options);
        var repo = BuildRepo(LibraryAccess.AllowOnly(new[] { _libA.Id }), db);

        Assert.False(await repo.ExistsAsync(_libB.Id));
        Assert.True(await repo.ExistsAsync(_libA.Id));
    }

    [Fact]
    public async Task GetLibraryItemsAsync_BlockedLibrary_ReturnsEmptyPageWithZeroCount()
    {
        using var db = new AppDbContext(_options);
        var repo = BuildRepo(LibraryAccess.AllowOnly(new[] { _libA.Id }), db);

        var page = await repo.GetLibraryItemsAsync(_libB.Id, new LibraryItemFilter
        {
            Page = 1, PageSize = 50, UserId = Guid.NewGuid()
        });

        Assert.Empty(page.Items);
        Assert.Equal(0, page.TotalCount);
    }

    [Fact]
    public async Task GetLibraryItemsAsync_AllowedLibrary_ReturnsItems()
    {
        using var db = new AppDbContext(_options);
        var repo = BuildRepo(LibraryAccess.AllowOnly(new[] { _libA.Id }), db);

        var page = await repo.GetLibraryItemsAsync(_libA.Id, new LibraryItemFilter
        {
            Page = 1, PageSize = 50, UserId = Guid.NewGuid()
        });

        Assert.Equal(2, page.TotalCount);
    }

    [Fact]
    public async Task IsPathUsedAsync_DoesNotApplyAclFilter()
    {
        // The path-collision check is admin-only. A user with restricted ACL
        // shouldn't reach this method, but the implementation must NOT mask
        // collisions even if it ever does. Confirms the method sees the
        // existing path despite the user's ACL excluding _libB.
        using var db = new AppDbContext(_options);
        var repo = BuildRepo(LibraryAccess.AllowOnly(new[] { _libA.Id }), db);

        Assert.True(await repo.IsPathUsedAsync("/a"));
        Assert.True(await repo.IsPathUsedAsync("/b"));
        Assert.False(await repo.IsPathUsedAsync("/never-used"));
    }
}
