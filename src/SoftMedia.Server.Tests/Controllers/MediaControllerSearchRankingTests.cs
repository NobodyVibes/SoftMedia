using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SoftMedia.Server.Controllers;
using SoftMedia.Server.Data;
using SoftMedia.Server.DTOs;
using SoftMedia.Server.Models;
using SoftMedia.Server.Services.Abstractions;
using SoftMedia.Server.Services.Media;
using SoftMedia.Server.Services.Security.ContentRating;
using SoftMedia.Server.Services.Security.LibraryAccess;
using Xunit;

namespace SoftMedia.Server.Tests.Controllers;

/// <summary>
/// Global search ranking. Runs against SQLITE, not EF InMemory: the endpoint
/// now issues a distinct-libraries query plus one ordered query per matching
/// library, and the in-memory provider would evaluate all of it client-side —
/// passing even if none of the LIKE/ordering expressions translate to SQL.
///
/// The behaviour under test is the ranking contract the dropdown relies on:
///   - groups arrive ordered by best match tier, then library position;
///   - every matching library is represented (the old flat global cap let one
///     strong library push the rest out of the response entirely);
///   - tier-2 items carry a "why is this here" reason.
/// </summary>
public class MediaControllerSearchRankingTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AppDbContext _db;
    private readonly Library _first;   // Order 0
    private readonly Library _second;  // Order 1

    public MediaControllerSearchRankingTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;
        _db = new AppDbContext(options);
        _db.Database.EnsureCreated();

        _first = new Library { Id = Guid.NewGuid(), Name = "Movies", Type = LibraryType.Movie, Order = 0, Paths = new() { "/movies" } };
        _second = new Library { Id = Guid.NewGuid(), Name = "Music", Type = LibraryType.Music, Order = 1, Paths = new() { "/music" } };
        _db.Libraries.AddRange(_first, _second);
        _db.SaveChanges();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private MediaController NewController()
    {
        var access = new Mock<IUserLibraryAccessProvider>();
        access.Setup(a => a.GetCurrentAsync()).ReturnsAsync(LibraryAccess.Unrestricted);
        var ratings = new Mock<IUserContentRatingProvider>();
        ratings.Setup(r => r.GetCurrentAsync()).ReturnsAsync(UserRatingCeilings.Unrestricted);

        return new MediaController(
            _db,
            Mock.Of<IMediaRetrievalService>(),
            Mock.Of<IRecommendationService>(),
            access.Object,
            ratings.Object,
            NullLogger<MediaController>.Instance);
    }

    private MediaItem Seed(Library lib, string title, MediaType type = MediaType.Movie, string? overview = null)
    {
        var item = new MediaItem
        {
            Id = Guid.NewGuid(),
            LibraryId = lib.Id,
            Title = title,
            SortTitle = title,
            Path = $"/{lib.Name}/{title}",
            Type = type,
            Overview = overview,
        };
        _db.MediaItems.Add(item);
        _db.SaveChanges();
        return item;
    }

    private async Task<List<GlobalSearchResultDto>> SearchAsync(string query, int limit = 5)
    {
        var result = await NewController().GlobalSearch(query, limit);
        return Assert.IsAssignableFrom<List<GlobalSearchResultDto>>(
            ((OkObjectResult)result.Result!).Value);
    }

    // ── Group ordering ───────────────────────────────────────────────────────

    [Fact]
    public async Task Groups_OrderByMatchQuality_NotLibraryPosition()
    {
        // The first-positioned library only has a description match (tier 2);
        // the second-positioned one has a title-prefix hit (tier 0). Quality
        // must beat position.
        Seed(_first, "Some Film", overview: "a test of courage");
        Seed(_second, "Test Anthem", MediaType.Audio);

        var groups = await SearchAsync("test");

        Assert.Equal(2, groups.Count);
        Assert.Equal(_second.Id, groups[0].LibraryId);
        Assert.Equal(0, groups[0].BestMatchTier);
        Assert.Equal(2, groups[1].BestMatchTier);
    }

    [Fact]
    public async Task Groups_TieOnQuality_BreaksByLibraryPosition()
    {
        Seed(_first, "Test Film");
        Seed(_second, "Test Anthem", MediaType.Audio);

        var groups = await SearchAsync("test");

        Assert.Equal(new[] { _first.Id, _second.Id }, groups.Select(g => g.LibraryId));
    }

    // ── No vanishing libraries ───────────────────────────────────────────────

    [Fact]
    public async Task EveryMatchingLibraryIsRepresented_EvenPastTheOldGlobalCap()
    {
        // 30 strong hits in one library used to consume the whole flat cap
        // (limit * 5 = 25), so the other library's single hit fell off the end
        // and the library disappeared from the dropdown.
        for (var i = 0; i < 30; i++) Seed(_first, $"Test Movie {i:D2}");
        Seed(_second, "Test Anthem", MediaType.Audio);

        var groups = await SearchAsync("test");

        Assert.Contains(groups, g => g.LibraryId == _second.Id);
        // And each group is still capped at the per-group limit.
        Assert.All(groups, g => Assert.True(g.Items.Count <= 5));
    }

    [Fact]
    public async Task WithinAGroup_TitleTierOutranksAlphabet()
    {
        Seed(_first, "A Movie About Nothing", overview: "test footage");
        Seed(_first, "Test Film");
        Seed(_first, "Contest Night"); // contains, not prefix

        var groups = await SearchAsync("test");
        var titles = groups.Single().Items.Select(i => i.Title).ToList();

        Assert.Equal(new[] { "Test Film", "Contest Night", "A Movie About Nothing" }, titles);
    }

    // ── Match reasons ────────────────────────────────────────────────────────

    [Fact]
    public async Task Tier2Items_CarryAReason_TitleMatchesDoNot()
    {
        var titled = Seed(_first, "Test Film");
        var described = Seed(_first, "Some Film", overview: "a test of courage");

        var group = (await SearchAsync("test")).Single();

        Assert.False(group.MatchReasons.ContainsKey(titled.Id.ToString()));
        Assert.Equal("Matched description", group.MatchReasons[described.Id.ToString()]);
    }

    [Fact]
    public async Task GenreMatch_NamesTheGenre()
    {
        var genre = new Genre { Name = "Testcore" };
        _db.Genres.Add(genre);
        var item = Seed(_first, "Some Film");
        _db.MediaItemGenres.Add(new MediaItemGenre { MediaItemId = item.Id, GenreId = genre.Id });
        _db.SaveChanges();

        var group = (await SearchAsync("test")).Single();

        Assert.Equal("Matched genre: Testcore", group.MatchReasons[item.Id.ToString()]);
    }

    [Fact]
    public async Task CastMatch_NamesThePerson()
    {
        var person = new Person { Name = "Ted Testa" };
        _db.Persons.Add(person);
        _db.SaveChanges();
        var item = Seed(_first, "Some Film");
        _db.MediaItemCasts.Add(new MediaItemCast { MediaItemId = item.Id, PersonId = person.Id });
        _db.SaveChanges();

        var group = (await SearchAsync("test")).Single();

        Assert.Equal("Matched cast: Ted Testa", group.MatchReasons[item.Id.ToString()]);
    }

    [Fact]
    public async Task TrackMatchedViaAlbum_NamesTheAlbum()
    {
        var album = Seed(_second, "Test Sessions", MediaType.Album);
        var track = new MediaItem
        {
            Id = Guid.NewGuid(), LibraryId = _second.Id, Title = "Opening Song",
            SortTitle = "Opening Song", Path = "/music/opening.flac",
            Type = MediaType.Audio, AlbumId = album.Id,
        };
        _db.MediaItems.Add(track);
        _db.SaveChanges();

        var group = (await SearchAsync("test")).Single(g => g.LibraryId == _second.Id);

        Assert.Equal("Matched album: Test Sessions", group.MatchReasons[track.Id.ToString()]);
    }

    // ── Fundamentals that must survive the refactor ─────────────────────────

    [Fact]
    public async Task DeniedLibraries_StayInvisible()
    {
        Seed(_first, "Test Film");
        Seed(_second, "Test Anthem", MediaType.Audio);

        var access = new Mock<IUserLibraryAccessProvider>();
        access.Setup(a => a.GetCurrentAsync())
            .ReturnsAsync(LibraryAccess.AllowOnly(new[] { _first.Id }));
        var ratings = new Mock<IUserContentRatingProvider>();
        ratings.Setup(r => r.GetCurrentAsync()).ReturnsAsync(UserRatingCeilings.Unrestricted);
        var controller = new MediaController(
            _db, Mock.Of<IMediaRetrievalService>(), Mock.Of<IRecommendationService>(),
            access.Object, ratings.Object, NullLogger<MediaController>.Instance);

        var result = await controller.GlobalSearch("test", 5);
        var groups = Assert.IsAssignableFrom<List<GlobalSearchResultDto>>(
            ((OkObjectResult)result.Result!).Value);

        Assert.Single(groups);
        Assert.Equal(_first.Id, groups[0].LibraryId);
    }

    [Fact]
    public async Task WildcardsInTheQuery_StayLiteral()
    {
        Seed(_first, "100% Test");
        Seed(_first, "Unrelated Film");

        var groups = await SearchAsync("0% t");

        Assert.Single(groups.Single().Items);
    }

    [Fact]
    public async Task ShortQueries_ReturnNothing()
    {
        Seed(_first, "Test Film");

        Assert.Empty(await SearchAsync("t"));
    }
}
