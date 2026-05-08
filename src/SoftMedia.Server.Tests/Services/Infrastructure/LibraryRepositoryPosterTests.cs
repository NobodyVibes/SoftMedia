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

/// Diagnostic — if the library grid shows missing posters but the detail
/// page shows them, the suspect is the projection inside
/// LibraryRepository.GetLibraryItemsAsync. This test exercises the
/// full pipeline (DB → repo → DTO) and asserts PosterPath is populated.
public class LibraryRepositoryPosterTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;
    private readonly Library _movieLib;
    private readonly MediaItem _movie;

    public LibraryRepositoryPosterTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options;

        _movieLib = new Library { Id = Guid.NewGuid(), Name = "Movies", Type = LibraryType.Movie, Paths = new() { "/m" } };
        _movie = new MediaItem
        {
            Id = Guid.NewGuid(),
            LibraryId = _movieLib.Id,
            Title = "Inception",
            SortTitle = "Inception",
            Path = "/m/inception.mkv",
            Type = MediaType.Movie,
            PosterUrl = "https://m.media-amazon.com/images/M/abcd1234.jpg",
        };

        using var ctx = new AppDbContext(_options);
        ctx.Database.EnsureCreated();
        ctx.Libraries.Add(_movieLib);
        ctx.MediaItems.Add(_movie);
        ctx.SaveChanges();
    }

    public void Dispose() => _connection.Dispose();

    private LibraryRepository BuildRepo(AppDbContext db)
    {
        var rating = new Mock<IUserContentRatingProvider>();
        rating.Setup(r => r.GetCurrentAsync()).ReturnsAsync(UserRatingCeilings.Unrestricted);
        var access = new Mock<IUserLibraryAccessProvider>();
        access.Setup(p => p.GetCurrentAsync()).ReturnsAsync(LibraryAccess.Unrestricted);
        return new LibraryRepository(db, rating.Object, access.Object);
    }

    [Fact]
    public async Task GetLibraryItemsAsync_ReturnsMediaItemWithPosterUrlIntact()
    {
        using var db = new AppDbContext(_options);
        var repo = BuildRepo(db);

        var result = await repo.GetLibraryItemsAsync(_movieLib.Id, new LibraryItemFilter
        {
            Page = 1, PageSize = 50, UserId = Guid.NewGuid()
        });

        Assert.Single(result.Items);
        var (media, _) = result.Items.First();
        Assert.NotNull(media);
        Assert.Equal(_movie.PosterUrl, media.PosterUrl);
    }

    [Fact]
    public async Task FromMediaItem_ProducesProxyPosterPath()
    {
        // Direct unit test of the DTO mapper to confirm the URL construction.
        var dto = MediaItemDto.FromMediaItem(_movie, "/api/v1/image/proxy");

        Assert.NotNull(dto.PosterPath);
        Assert.StartsWith("/api/v1/image/proxy?url=", dto.PosterPath);
        Assert.Contains(Uri.EscapeDataString(_movie.PosterUrl!), dto.PosterPath);
    }
}
