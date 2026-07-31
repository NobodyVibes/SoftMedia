using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SoftMedia.Server.Data;
using SoftMedia.Server.DTOs;
using SoftMedia.Server.Models;
using SoftMedia.Server.Services.Abstractions;
using SoftMedia.Server.Services.Infrastructure;
using SoftMedia.Server.Services.Media;
using SoftMedia.Server.Services.Scanning;
using SoftMedia.Server.Services.Security.ContentRating;
using SoftMedia.Server.Services.Security.LibraryAccess;
using Xunit;

namespace SoftMedia.Server.Tests.Services.Media;

/// <summary>
/// DV-WI-015 — collapsed read paths on REAL SQLite (the OnePerVersionGroup predicate is
/// a deep correlated subquery; EF InMemory proving it would prove nothing): the library
/// grid and search show ONE card per version group (the computed primary), the episode
/// list shows one row per episode with group-level watched/resume and VersionCount, and
/// the watched reconciliation aligns legacy rows.
/// </summary>
public class VersionGroupReadPathTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;
    private readonly Guid _userId = Guid.NewGuid();
    private readonly Guid _libraryId = Guid.NewGuid();
    private readonly Mock<IUserContentRatingProvider> _ratings = new();
    private readonly Mock<IUserLibraryAccessProvider> _access = new();

    public VersionGroupReadPathTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;
        using var ctx = new AppDbContext(_options);
        ctx.Database.EnsureCreated();
        ctx.Users.Add(new User
        {
            Id = _userId, Username = "viewer", PasswordHash = "x", Role = UserRole.User,
            IsApproved = true, CreatedAt = DateTime.UtcNow, FirstName = "T", LastName = "T", ContentRatings = "{}",
        });
        ctx.Libraries.Add(new Library { Id = _libraryId, Name = "Movies", Type = LibraryType.Movie });
        ctx.SaveChanges();

        _ratings.Setup(r => r.GetCurrentAsync()).ReturnsAsync(UserRatingCeilings.Unrestricted);
        _access.Setup(a => a.GetCurrentAsync()).ReturnsAsync(LibraryAccess.Unrestricted);
    }

    public void Dispose() => _connection.Dispose();

    private AppDbContext NewContext() => new(_options);

    private MediaItem AddItem(AppDbContext ctx, string title, MediaType type, Guid? groupId,
        int? height = null, bool preferred = false, Guid? seriesId = null, int? season = null, int? episode = null)
    {
        var m = new MediaItem
        {
            Id = Guid.NewGuid(), LibraryId = _libraryId, Type = type, Title = title,
            VersionGroupId = groupId, Height = height, PreferredVersion = preferred,
            SeriesId = seriesId, SeasonNumber = season, EpisodeNumber = episode,
            Path = $"/x/{title}-{height}-{Guid.NewGuid():N}.mkv", Duration = 1000,
        };
        ctx.MediaItems.Add(m);
        ctx.SaveChanges();
        return m;
    }

    [Fact]
    public async Task OnePerVersionGroup_KeepsThePrimary_AndUngroupedRows()
    {
        var group = Guid.NewGuid();
        using var ctx = NewContext();
        AddItem(ctx, "Tenet", MediaType.Movie, group, height: 1080);
        var best = AddItem(ctx, "Tenet", MediaType.Movie, group, height: 2160);
        var single = AddItem(ctx, "Alone", MediaType.Movie, null, height: 1080);

        var visible = await ctx.MediaItems.AsNoTracking()
            .Where(m => m.Type == MediaType.Movie)
            .OnePerVersionGroup(ctx.MediaItems.AsNoTracking())
            .Select(m => m.Id)
            .ToListAsync();

        Assert.Equal(2, visible.Count);
        Assert.Contains(best.Id, visible);
        Assert.Contains(single.Id, visible);
    }

    [Fact]
    public async Task OnePerVersionGroup_PreferredOverrideBeatsResolution()
    {
        var group = Guid.NewGuid();
        using var ctx = NewContext();
        var preferred = AddItem(ctx, "Tenet", MediaType.Movie, group, height: 1080, preferred: true);
        AddItem(ctx, "Tenet", MediaType.Movie, group, height: 2160);

        var visible = await ctx.MediaItems.AsNoTracking()
            .OnePerVersionGroup(ctx.MediaItems.AsNoTracking())
            .Where(m => m.Type == MediaType.Movie)
            .Select(m => m.Id)
            .ToListAsync();

        Assert.Equal(preferred.Id, Assert.Single(visible));
    }

    [Fact]
    public async Task LibraryGrid_ShowsOneCardPerGroup_WithExactCountsAndVersionCount()
    {
        var group = Guid.NewGuid();
        using (var seed = NewContext())
        {
            AddItem(seed, "Tenet", MediaType.Movie, group, height: 1080);
            AddItem(seed, "Tenet", MediaType.Movie, group, height: 2160);
            AddItem(seed, "Alone", MediaType.Movie, null);
        }

        var ctx = NewContext();
        var repo = new LibraryRepository(ctx, _ratings.Object, _access.Object);
        var result = await repo.GetLibraryItemsAsync(_libraryId, new LibraryItemFilter());

        Assert.Equal(2, result.TotalCount); // pagination math counts titles, not files
        Assert.Equal(2, result.Items.Count());
        var tenet = result.Items.Single(x => x.Media.Title == "Tenet").Media;
        Assert.Equal(2160, tenet.Height); // the computed primary fronts the group
    }

    [Fact]
    public async Task EpisodeList_CollapsesToOneRowPerEpisode_WithGroupWatchedAndResume()
    {
        var seriesId = Guid.NewGuid();
        var group = Guid.NewGuid();
        using (var seed = NewContext())
        {
            seed.MediaItems.Add(new MediaItem { Id = seriesId, LibraryId = _libraryId, Type = MediaType.Series, Title = "Show" });
            seed.SaveChanges();
            var hd = AddItem(seed, "E1", MediaType.Episode, group, height: 1080, seriesId: seriesId, season: 1, episode: 1);
            AddItem(seed, "E1", MediaType.Episode, group, height: 2160, seriesId: seriesId, season: 1, episode: 1);
            AddItem(seed, "E2", MediaType.Episode, Guid.NewGuid(), height: 1080, seriesId: seriesId, season: 1, episode: 2);
            // The user watched (and part-rewatched) only the 1080p copy.
            seed.UserMediaInteractions.Add(new UserMediaInteraction
            {
                UserId = _userId, MediaItemId = hd.Id, IsWatched = true,
                PlaybackPosition = 480, LastPlayed = DateTime.UtcNow,
            });
            seed.SaveChanges();
        }

        var service = BuildLibraryService(out var ctx);
        using (ctx)
        {
            var episodes = (await service.GetSeriesEpisodesAsync(seriesId, _userId)).ToList();

            Assert.Equal(2, episodes.Count); // E1 (collapsed) + E2
            var e1 = episodes[0];
            Assert.Equal(1, e1.EpisodeNumber);
            Assert.Equal(2160, e1.Height);          // primary copy fronts the row
            Assert.Equal(2, e1.VersionCount);
            Assert.True(e1.Watched);                // any-copy-watched
            Assert.Equal(480, e1.PlaybackPosition); // resume from the played copy
            Assert.Equal(1, episodes[1].VersionCount);
        }
    }

    [Fact]
    public async Task ReconcileGroupWatched_AlignsExistingRows_PerUserPerGroup()
    {
        var group = Guid.NewGuid();
        var otherUser = Guid.NewGuid();
        Guid hdId, uhdId;
        using (var seed = NewContext())
        {
            seed.Users.Add(new User
            {
                Id = otherUser, Username = "other", PasswordHash = "x", Role = UserRole.User,
                IsApproved = true, CreatedAt = DateTime.UtcNow, FirstName = "O", LastName = "O", ContentRatings = "{}",
            });
            hdId = AddItem(seed, "Tenet", MediaType.Movie, group, height: 1080).Id;
            uhdId = AddItem(seed, "Tenet", MediaType.Movie, group, height: 2160).Id;
            seed.UserMediaInteractions.AddRange(
                new UserMediaInteraction { UserId = _userId, MediaItemId = hdId, IsWatched = true },
                new UserMediaInteraction { UserId = _userId, MediaItemId = uhdId, IsWatched = false, PlaybackPosition = 300 },
                new UserMediaInteraction { UserId = otherUser, MediaItemId = uhdId, IsWatched = false, PlaybackPosition = 200 });
            seed.SaveChanges();
        }

        using var ctx = NewContext();
        var changed = await VersionGroupAssigner.ReconcileGroupWatchedAsync(ctx, onlyGroupIds: null);
        await ctx.SaveChangesAsync();

        Assert.Equal(1, changed);
        using var verify = NewContext();
        Assert.True(verify.UserMediaInteractions.Single(i => i.UserId == _userId && i.MediaItemId == uhdId).IsWatched);
        // The other user never watched any copy — untouched.
        Assert.False(verify.UserMediaInteractions.Single(i => i.UserId == otherUser).IsWatched);
    }

    private LibraryService BuildLibraryService(out AppDbContext ctx)
    {
        ctx = NewContext();
        var mediaRepo = new MediaRepository(ctx, _ratings.Object, _access.Object);
        return new LibraryService(
            Mock.Of<ILibraryRepository>(),
            mediaRepo,
            Mock.Of<ILibraryScanQueueService>(),
            Mock.Of<IImageCacheService>(),
            new LibraryWatcher(new Mock<IServiceScopeFactory>().Object, NullLogger<LibraryWatcher>.Instance),
            ctx,
            _access.Object,
            _ratings.Object,
            Mock.Of<ILibraryCleanupService>(),
            NullLogger<LibraryService>.Instance);
    }
}
