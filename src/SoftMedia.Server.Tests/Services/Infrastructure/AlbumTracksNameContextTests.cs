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

/// B-03 — album-page playback showed "Unknown Artist": the album-tracks query
/// joined interactions through a projection without loading the Artist/Album
/// navigations, so BuildNameContext produced no metadata for the player bar.
public class AlbumTracksNameContextTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;
    private readonly Guid _albumId;

    public AlbumTracksNameContextTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options;

        var lib = new Library { Id = Guid.NewGuid(), Name = "Music", Type = LibraryType.Music, Paths = new() { "/music" } };
        var artist = new MediaItem { Id = Guid.NewGuid(), LibraryId = lib.Id, Title = "The Testers", SortTitle = "Testers", Path = "/music/testers", Type = MediaType.Artist };
        var album = new MediaItem { Id = Guid.NewGuid(), LibraryId = lib.Id, Title = "Greatest Mocks", SortTitle = "Greatest Mocks", Path = "/music/testers/gm", Type = MediaType.Album, ArtistId = artist.Id };
        var track = new MediaItem
        {
            Id = Guid.NewGuid(), LibraryId = lib.Id, Title = "Track One", SortTitle = "Track One",
            Path = "/music/testers/gm/01.mp3", Type = MediaType.Audio,
            ArtistId = artist.Id, AlbumId = album.Id, TrackNumber = 1,
        };
        _albumId = album.Id;

        using var ctx = new AppDbContext(_options);
        ctx.Database.EnsureCreated();
        ctx.Libraries.Add(lib);
        ctx.MediaItems.AddRange(artist, album, track);
        ctx.SaveChanges();
    }

    public void Dispose() => _connection.Dispose();

    [Fact]
    public async Task AlbumTracks_CarryArtistAndAlbumNameContext()
    {
        using var db = new AppDbContext(_options);
        var rating = new Mock<IUserContentRatingProvider>();
        rating.Setup(r => r.GetCurrentAsync()).ReturnsAsync(UserRatingCeilings.Unrestricted);
        var access = new Mock<IUserLibraryAccessProvider>();
        access.Setup(p => p.GetCurrentAsync()).ReturnsAsync(LibraryAccess.Unrestricted);
        var repo = new MediaRepository(db, rating.Object, access.Object);

        var tracks = (await repo.GetAlbumTracksWithInteractionsAsync(_albumId, Guid.NewGuid())).ToList();

        var media = Assert.Single(tracks).Media;
        // The navigations must survive the interaction join's projection…
        Assert.NotNull(media.Artist);
        Assert.NotNull(media.Album);

        // …so the DTO the player bar consumes actually names the artist/album.
        var dto = MediaItemDto.FromMediaItem(media);
        Assert.NotNull(dto.Metadata);
        Assert.Equal("The Testers", dto.Metadata!["artist"]);
        Assert.Equal("Greatest Mocks", dto.Metadata["album"]);
    }
}
