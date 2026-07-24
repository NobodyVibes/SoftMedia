using Microsoft.EntityFrameworkCore;
using Moq;
using SoftMedia.Server.Data;
using SoftMedia.Server.Models;
using SoftMedia.Server.Services.Infrastructure;
using SoftMedia.Server.Services.Security.ContentRating;
using SoftMedia.Server.Services.Security.LibraryAccess;
using Xunit;

namespace SoftMedia.Server.Tests.Services.Infrastructure;

/// <summary>
/// SR-WI-065 (API-M7) — GetRecentMediaAsync rolls episodes/seasons up to their
/// series and audio tracks up to their album INSIDE the query (previously it
/// fetched limit*25 hydrated rows and left dedup to MediaRetrievalService).
/// These tests pin the rollup semantics and deliberately run on the InMemory
/// provider so the grouped/CASE query shape stays compatible with the provider
/// the wider suite uses (the ACL tests cover the same method on SQLite).
/// </summary>
public class MediaRepositoryRecentTests
{
    private readonly DbContextOptions<AppDbContext> _options;
    private readonly Guid _movieLibId = Guid.NewGuid();
    private readonly Guid _showLibId = Guid.NewGuid();

    public MediaRepositoryRecentTests()
    {
        _options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"recent-{Guid.NewGuid():N}")
            .Options;

        using var ctx = new AppDbContext(_options);
        ctx.Libraries.Add(new Library { Id = _movieLibId, Name = "Movies", Type = LibraryType.Movie, Paths = new() { "/movies" } });
        ctx.Libraries.Add(new Library { Id = _showLibId, Name = "Shows", Type = LibraryType.TV, Paths = new() { "/shows" } });
        ctx.SaveChanges();
    }

    private MediaRepository BuildRepo(AppDbContext db)
    {
        var rating = new Mock<IUserContentRatingProvider>();
        rating.Setup(r => r.GetCurrentAsync()).ReturnsAsync(UserRatingCeilings.Unrestricted);
        var access = new Mock<IUserLibraryAccessProvider>();
        access.Setup(p => p.GetCurrentAsync()).ReturnsAsync(LibraryAccess.Unrestricted);
        return new MediaRepository(db, rating.Object, access.Object);
    }

    private static MediaItem Item(
        Guid libraryId, MediaType type, string title, DateTime added,
        Guid? id = null, Guid? seriesId = null, Guid? albumId = null)
        => new()
        {
            Id = id ?? Guid.NewGuid(),
            LibraryId = libraryId,
            Type = type,
            Title = title,
            SortTitle = title,
            Path = "/p/" + Guid.NewGuid().ToString("N"),
            DateAdded = added,
            SeriesId = seriesId,
            AlbumId = albumId,
        };

    private static readonly DateTime T0 = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static DateTime T(int days) => T0.AddDays(days);

    [Fact]
    public async Task Episodes_RollUpToOneSeriesEntry_WithDateAddedPromotedToNewestEpisode()
    {
        var seriesId = Guid.NewGuid();
        using (var ctx = new AppDbContext(_options))
        {
            ctx.MediaItems.Add(Item(_showLibId, MediaType.Series, "Show", T(0), id: seriesId));
            ctx.MediaItems.Add(Item(_showLibId, MediaType.Episode, "E1", T(1), seriesId: seriesId));
            ctx.MediaItems.Add(Item(_showLibId, MediaType.Episode, "E2", T(3), seriesId: seriesId));
            ctx.MediaItems.Add(Item(_movieLibId, MediaType.Movie, "Movie", T(2)));
            ctx.SaveChanges();
        }

        using var db = new AppDbContext(_options);
        var recent = (await BuildRepo(db).GetRecentMediaAsync(10, type: null)).ToList();

        // One series entry (no raw episodes), newest activity first.
        Assert.Equal(2, recent.Count);
        Assert.Equal(seriesId, recent[0].Id);
        Assert.Equal(MediaType.Series, recent[0].Type);
        Assert.Equal(T(3), recent[0].DateAdded); // promoted from newest episode
        Assert.Equal("Movie", recent[1].Title);
        Assert.DoesNotContain(recent, m => m.Type == MediaType.Episode);
    }

    [Fact]
    public async Task AudioTracks_RollUpToOneAlbumEntry_WithDateAddedPromoted()
    {
        var albumId = Guid.NewGuid();
        using (var ctx = new AppDbContext(_options))
        {
            ctx.MediaItems.Add(Item(_movieLibId, MediaType.Album, "Album", T(0), id: albumId));
            ctx.MediaItems.Add(Item(_movieLibId, MediaType.Audio, "Track 1", T(4), albumId: albumId));
            ctx.MediaItems.Add(Item(_movieLibId, MediaType.Audio, "Track 2", T(5), albumId: albumId));
            ctx.SaveChanges();
        }

        using var db = new AppDbContext(_options);
        var recent = (await BuildRepo(db).GetRecentMediaAsync(10, type: null)).ToList();

        var entry = Assert.Single(recent);
        Assert.Equal(albumId, entry.Id);
        Assert.Equal(MediaType.Album, entry.Type);
        Assert.Equal(T(5), entry.DateAdded);
    }

    [Fact]
    public async Task EpisodeBurst_DoesNotStarveOtherContentOutOfTheLimitWindow()
    {
        // The pre-rewrite over-fetch scanned at most limit*25 raw rows; a large
        // enough episode burst pushed everything else out of that window. The
        // query-side rollup consumes ONE result slot per series regardless of
        // how many episodes arrived.
        var seriesId = Guid.NewGuid();
        using (var ctx = new AppDbContext(_options))
        {
            ctx.MediaItems.Add(Item(_showLibId, MediaType.Series, "Big Show", T(0), id: seriesId));
            for (var i = 0; i < 120; i++)
                ctx.MediaItems.Add(Item(_showLibId, MediaType.Episode, $"E{i}", T(50).AddMinutes(i), seriesId: seriesId));
            ctx.MediaItems.Add(Item(_movieLibId, MediaType.Movie, "Older Movie A", T(10)));
            ctx.MediaItems.Add(Item(_movieLibId, MediaType.Movie, "Older Movie B", T(20)));
            ctx.SaveChanges();
        }

        using var db = new AppDbContext(_options);
        var recent = (await BuildRepo(db).GetRecentMediaAsync(3, type: null)).ToList();

        Assert.Equal(3, recent.Count);
        Assert.Equal(seriesId, recent[0].Id);
        Assert.Equal("Older Movie B", recent[1].Title);
        Assert.Equal("Older Movie A", recent[2].Title);
    }

    [Fact]
    public async Task Episode_WithoutSeries_IsDropped_LikeTheOldInMemoryDedup()
    {
        using (var ctx = new AppDbContext(_options))
        {
            ctx.MediaItems.Add(Item(_showLibId, MediaType.Episode, "Orphan Episode", T(9)));
            ctx.MediaItems.Add(Item(_movieLibId, MediaType.Movie, "Movie", T(1)));
            ctx.SaveChanges();
        }

        using var db = new AppDbContext(_options);
        var recent = (await BuildRepo(db).GetRecentMediaAsync(10, type: null)).ToList();

        var entry = Assert.Single(recent);
        Assert.Equal("Movie", entry.Title);
    }

    [Fact]
    public async Task SeriesRowNewerThanItsEpisodes_KeepsItsOwnDateAdded()
    {
        var seriesId = Guid.NewGuid();
        using (var ctx = new AppDbContext(_options))
        {
            ctx.MediaItems.Add(Item(_showLibId, MediaType.Series, "Show", T(8), id: seriesId));
            ctx.MediaItems.Add(Item(_showLibId, MediaType.Episode, "E1", T(2), seriesId: seriesId));
            ctx.SaveChanges();
        }

        using var db = new AppDbContext(_options);
        var recent = (await BuildRepo(db).GetRecentMediaAsync(10, type: null)).ToList();

        var entry = Assert.Single(recent);
        Assert.Equal(seriesId, entry.Id);
        Assert.Equal(T(8), entry.DateAdded); // max over group = the series row itself
    }

    [Fact]
    public async Task TypeFilter_StillNarrowsToLibrariesOfThatType()
    {
        var seriesId = Guid.NewGuid();
        using (var ctx = new AppDbContext(_options))
        {
            ctx.MediaItems.Add(Item(_showLibId, MediaType.Series, "Show", T(0), id: seriesId));
            ctx.MediaItems.Add(Item(_showLibId, MediaType.Episode, "E1", T(5), seriesId: seriesId));
            ctx.MediaItems.Add(Item(_movieLibId, MediaType.Movie, "Movie", T(1)));
            ctx.SaveChanges();
        }

        using var db = new AppDbContext(_options);
        var recent = (await BuildRepo(db).GetRecentMediaAsync(10, LibraryType.Movie)).ToList();

        var entry = Assert.Single(recent);
        Assert.Equal("Movie", entry.Title);
    }

    [Fact]
    public async Task Limit_CapsTheResult_NewestFirst()
    {
        using (var ctx = new AppDbContext(_options))
        {
            for (var i = 1; i <= 5; i++)
                ctx.MediaItems.Add(Item(_movieLibId, MediaType.Movie, $"M{i}", T(i)));
            ctx.SaveChanges();
        }

        using var db = new AppDbContext(_options);
        var recent = (await BuildRepo(db).GetRecentMediaAsync(3, type: null)).ToList();

        Assert.Equal(new[] { "M5", "M4", "M3" }, recent.Select(m => m.Title).ToArray());
    }

    [Fact]
    public async Task MissingItems_AreExcludedFromTheRollup()
    {
        var seriesId = Guid.NewGuid();
        using (var ctx = new AppDbContext(_options))
        {
            ctx.MediaItems.Add(Item(_showLibId, MediaType.Series, "Show", T(0), id: seriesId));
            var missingEp = Item(_showLibId, MediaType.Episode, "Gone", T(9), seriesId: seriesId);
            missingEp.IsMissing = true;
            ctx.MediaItems.Add(missingEp);
            ctx.MediaItems.Add(Item(_showLibId, MediaType.Episode, "Here", T(4), seriesId: seriesId));
            ctx.SaveChanges();
        }

        using var db = new AppDbContext(_options);
        var recent = (await BuildRepo(db).GetRecentMediaAsync(10, type: null)).ToList();

        var entry = Assert.Single(recent);
        Assert.Equal(seriesId, entry.Id);
        // Promotion must come from the newest VISIBLE episode, not the missing one.
        Assert.Equal(T(4), entry.DateAdded);
    }
}
