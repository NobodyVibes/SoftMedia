using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Moq;
using SoftMedia.Server.Data;
using SoftMedia.Server.DTOs;
using SoftMedia.Server.Models;
using SoftMedia.Server.Services.Abstractions;
using SoftMedia.Server.Services.Media;
using SoftMedia.Server.Services.Security.ContentRating;
using SoftMedia.Server.Services.Security.LibraryAccess;
using Xunit;

namespace SoftMedia.Server.Tests.Services.Media;

/// <summary>
/// The Continue Watching row's contract:
///   - per-user, newest-played first;
///   - unfinished MOVIES appear and near-finished ones (watched flag / credits / 95%) do not;
///   - EPISODES never appear individually — the SERIES is the card, resuming via the shared
///     next-episode resolver, so a finished episode keeps the show in the row while a finished
///     series removes it;
///   - per-library ACL strips cards the user cannot access.
/// </summary>
public class ContinueWatchingServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;
    private readonly Mock<IRecommendationService> _recommendations = new();
    private readonly Mock<IUserLibraryAccessProvider> _access = new();
    private readonly Mock<IUserContentRatingProvider> _ratings = new();

    private readonly Guid _userId = Guid.NewGuid();
    private readonly Library _movieLib = new() { Id = Guid.NewGuid(), Name = "Movies", Type = LibraryType.Movie };
    private readonly Library _tvLib = new() { Id = Guid.NewGuid(), Name = "TV", Type = LibraryType.TV };

    public ContinueWatchingServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;

        using var ctx = new AppDbContext(_options);
        ctx.Database.EnsureCreated();
        ctx.Users.Add(new User
        {
            Id = _userId,
            Username = "viewer",
            PasswordHash = "x",
            Role = UserRole.User,
            IsApproved = true,
            CreatedAt = DateTime.UtcNow,
            FirstName = "T",
            LastName = "T",
            ContentRatings = "{}",
        });
        ctx.Libraries.AddRange(_movieLib, _tvLib);
        ctx.SaveChanges();

        // Default: unrestricted user. Individual tests override the access mock as needed.
        _access.Setup(a => a.GetCurrentAsync()).ReturnsAsync(LibraryAccess.Unrestricted);
        _ratings.Setup(r => r.GetCurrentAsync()).ReturnsAsync(UserRatingCeilings.Unrestricted);
    }

    public void Dispose() => _connection.Dispose();

    private ContinueWatchingService Build()
        => new(new AppDbContext(_options), _recommendations.Object, _access.Object, _ratings.Object);

    private MediaItem AddMovie(string title, double duration = 3600, double? creditsStart = null)
    {
        var m = new MediaItem
        {
            Id = Guid.NewGuid(), LibraryId = _movieLib.Id, Type = MediaType.Movie,
            Title = title, Duration = duration, CreditsStart = creditsStart,
        };
        using var ctx = new AppDbContext(_options);
        ctx.MediaItems.Add(m);
        ctx.SaveChanges();
        return m;
    }

    private (MediaItem Series, MediaItem Episode) AddSeriesWithEpisode(string title, double epDuration = 1500)
    {
        var series = new MediaItem { Id = Guid.NewGuid(), LibraryId = _tvLib.Id, Type = MediaType.Series, Title = title };
        var ep = new MediaItem
        {
            Id = Guid.NewGuid(), LibraryId = _tvLib.Id, Type = MediaType.Episode,
            Title = title + " S1E1", SeriesId = series.Id, SeasonNumber = 1, EpisodeNumber = 1, Duration = epDuration,
        };
        using var ctx = new AppDbContext(_options);
        ctx.MediaItems.AddRange(series, ep);
        ctx.SaveChanges();
        return (series, ep);
    }

    private void AddInteraction(Guid mediaId, double? position, DateTime lastPlayed, bool isWatched = false)
    {
        using var ctx = new AppDbContext(_options);
        ctx.UserMediaInteractions.Add(new UserMediaInteraction
        {
            UserId = _userId, MediaItemId = mediaId,
            PlaybackPosition = position, LastPlayed = lastPlayed, IsWatched = isWatched,
        });
        ctx.SaveChanges();
    }

    private void SetupResolver(Guid seriesId, Guid resumeEpisodeId, double resumePosition, bool seriesComplete = false)
        => _recommendations
            .Setup(r => r.GetNextEpisodeAsync(_userId, seriesId))
            .ReturnsAsync(new NextEpisodeResponse
            {
                EpisodeId = resumeEpisodeId,
                SeriesId = seriesId,
                ResumePosition = resumePosition,
                IsSeriesComplete = seriesComplete,
            });

    // ─────────────────────────────────────────────────────────────── Movies

    [Fact]
    public async Task Movie_in_progress_is_included_with_resume_progress()
    {
        var movie = AddMovie("Halfway", duration: 3600);
        AddInteraction(movie.Id, position: 1200, DateTime.UtcNow);

        var row = await Build().GetContinueWatchingAsync(_userId, 20);

        var entry = Assert.Single(row);
        Assert.Equal(movie.Id, entry.Id);
        Assert.Equal(1200, entry.PlaybackPosition);
        Assert.NotNull(entry.Progress);
        Assert.Equal(33.3, entry.Progress!.Value, 1);
    }

    [Fact]
    public async Task Movie_marked_watched_is_excluded()
    {
        var movie = AddMovie("Seen It");
        AddInteraction(movie.Id, position: 1200, DateTime.UtcNow, isWatched: true);

        Assert.Empty(await Build().GetContinueWatchingAsync(_userId, 20));
    }

    [Fact]
    public async Task Movie_past_95_percent_is_excluded_even_without_watched_flag()
    {
        var movie = AddMovie("Almost Done", duration: 3600);
        AddInteraction(movie.Id, position: 3500, DateTime.UtcNow); // ~97%

        Assert.Empty(await Build().GetContinueWatchingAsync(_userId, 20));
    }

    [Fact]
    public async Task Movie_past_credits_marker_is_excluded_before_95_percent()
    {
        // Credits start halfway through the file (e.g. a short feature with long credits):
        // crossing them counts as finished even though the raw fraction is far below 95%.
        var movie = AddMovie("Long Credits", duration: 3600, creditsStart: 1800);
        AddInteraction(movie.Id, position: 1900, DateTime.UtcNow);

        Assert.Empty(await Build().GetContinueWatchingAsync(_userId, 20));
    }

    // ─────────────────────────────────────────────────────────────── Series

    [Fact]
    public async Task Episodes_collapse_to_a_single_series_card_that_resumes_the_episode()
    {
        var (series, ep) = AddSeriesWithEpisode("Show");
        AddInteraction(ep.Id, position: 300, DateTime.UtcNow);
        SetupResolver(series.Id, ep.Id, resumePosition: 300);

        var row = await Build().GetContinueWatchingAsync(_userId, 20);

        var entry = Assert.Single(row);
        Assert.Equal(series.Id, entry.Id); // the SHOW is the card — never the episode
        Assert.Equal(300, entry.PlaybackPosition);
        Assert.NotNull(entry.Progress);
        Assert.Equal(20.0, entry.Progress!.Value, 1); // 300 / 1500s episode runtime
        _recommendations.Verify(r => r.GetNextEpisodeAsync(_userId, series.Id), Times.Once);
    }

    [Fact]
    public async Task Two_in_progress_episodes_of_one_series_yield_one_card()
    {
        var (series, ep1) = AddSeriesWithEpisode("Binge");
        var ep2 = new MediaItem
        {
            Id = Guid.NewGuid(), LibraryId = _tvLib.Id, Type = MediaType.Episode,
            Title = "Binge S1E2", SeriesId = series.Id, SeasonNumber = 1, EpisodeNumber = 2, Duration = 1500,
        };
        using (var ctx = new AppDbContext(_options)) { ctx.MediaItems.Add(ep2); ctx.SaveChanges(); }

        AddInteraction(ep1.Id, position: 700, DateTime.UtcNow.AddDays(-1));
        AddInteraction(ep2.Id, position: 200, DateTime.UtcNow);
        SetupResolver(series.Id, ep2.Id, resumePosition: 200);

        var row = await Build().GetContinueWatchingAsync(_userId, 20);

        Assert.Single(row);
        _recommendations.Verify(r => r.GetNextEpisodeAsync(_userId, series.Id), Times.Once);
    }

    [Fact]
    public async Task Finished_episode_keeps_the_series_in_the_row_resuming_the_next_episode()
    {
        // The user finished S1E1 (auto-marked watched at the credits, position reset to null) and
        // has not started S1E2. Finishing an EPISODE must not act like finishing the SERIES — the
        // show stays in the row and the resolver supplies the next episode as the resume target.
        var (series, ep) = AddSeriesWithEpisode("Nightly");
        var ep2Id = Guid.NewGuid();
        AddInteraction(ep.Id, position: null, DateTime.UtcNow, isWatched: true);
        SetupResolver(series.Id, ep2Id, resumePosition: 0);

        var row = await Build().GetContinueWatchingAsync(_userId, 20);

        var entry = Assert.Single(row);
        Assert.Equal(series.Id, entry.Id);
        Assert.Equal(0, entry.PlaybackPosition); // next episode starts fresh — no resume bar
    }

    [Fact]
    public async Task Fully_watched_series_is_excluded()
    {
        var (series, ep) = AddSeriesWithEpisode("Done Show");
        AddInteraction(ep.Id, position: null, DateTime.UtcNow, isWatched: true);
        SetupResolver(series.Id, ep.Id, resumePosition: 0, seriesComplete: true);

        Assert.Empty(await Build().GetContinueWatchingAsync(_userId, 20));
    }

    [Fact]
    public async Task Orphan_episode_without_series_is_its_own_card()
    {
        var orphan = new MediaItem
        {
            Id = Guid.NewGuid(), LibraryId = _tvLib.Id, Type = MediaType.Episode,
            Title = "Lone Special", Duration = 1500,
        };
        using (var ctx = new AppDbContext(_options)) { ctx.MediaItems.Add(orphan); ctx.SaveChanges(); }
        AddInteraction(orphan.Id, position: 400, DateTime.UtcNow);

        var row = await Build().GetContinueWatchingAsync(_userId, 20);

        var entry = Assert.Single(row);
        Assert.Equal(orphan.Id, entry.Id);
        _recommendations.Verify(
            r => r.GetNextEpisodeAsync(It.IsAny<Guid>(), It.IsAny<Guid>()), Times.Never);
    }

    // ─────────────────────────────────────────────────────────────── Ordering / scoping

    [Fact]
    public async Task Row_is_ordered_most_recently_played_first()
    {
        var older = AddMovie("Older");
        var (series, ep) = AddSeriesWithEpisode("Newer Show");
        var newest = AddMovie("Newest");

        AddInteraction(older.Id, position: 100, DateTime.UtcNow.AddDays(-2));
        AddInteraction(ep.Id, position: 100, DateTime.UtcNow.AddDays(-1));
        AddInteraction(newest.Id, position: 100, DateTime.UtcNow);
        SetupResolver(series.Id, ep.Id, resumePosition: 100);

        var row = await Build().GetContinueWatchingAsync(_userId, 20);

        Assert.Equal(new[] { newest.Id, series.Id, older.Id }, row.Select(r => r.Id).ToArray());
    }

    [Fact]
    public async Task Another_users_progress_never_leaks_into_the_row()
    {
        var movie = AddMovie("Someone Elses");
        var otherUser = Guid.NewGuid();
        using (var ctx = new AppDbContext(_options))
        {
            ctx.Users.Add(new User
            {
                Id = otherUser, Username = "other", PasswordHash = "x", Role = UserRole.User,
                IsApproved = true, CreatedAt = DateTime.UtcNow, FirstName = "O", LastName = "O", ContentRatings = "{}",
            });
            ctx.SaveChanges();
        }
        using (var ctx = new AppDbContext(_options))
        {
            ctx.UserMediaInteractions.Add(new UserMediaInteraction
            {
                UserId = otherUser, MediaItemId = movie.Id, PlaybackPosition = 500, LastPlayed = DateTime.UtcNow,
            });
            ctx.SaveChanges();
        }

        Assert.Empty(await Build().GetContinueWatchingAsync(_userId, 20));
    }

    [Fact]
    public async Task Card_in_a_library_blocked_by_ACL_is_dropped()
    {
        var movie = AddMovie("Blocked");
        AddInteraction(movie.Id, position: 500, DateTime.UtcNow);
        _access.Setup(a => a.GetCurrentAsync())
            .ReturnsAsync(LibraryAccess.AllowOnly(new[] { _tvLib.Id })); // movie library NOT allowed

        Assert.Empty(await Build().GetContinueWatchingAsync(_userId, 20));
    }

    [Fact]
    public async Task Watched_orphan_episode_is_excluded()
    {
        // A finished orphan episode (auto-marked at the credits: IsWatched=true, position reset)
        // qualifies as a candidate via the episode predicate but must be dropped by the explicit
        // watched flag — it has no series to advance to.
        var orphan = new MediaItem
        {
            Id = Guid.NewGuid(), LibraryId = _tvLib.Id, Type = MediaType.Episode,
            Title = "Finished Special", Duration = 1500,
        };
        using (var ctx = new AppDbContext(_options)) { ctx.MediaItems.Add(orphan); ctx.SaveChanges(); }
        AddInteraction(orphan.Id, position: 0, DateTime.UtcNow, isWatched: true);

        Assert.Empty(await Build().GetContinueWatchingAsync(_userId, 20));
    }

    [Fact]
    public async Task Blocked_item_does_not_consume_a_limit_slot()
    {
        // The NEWEST candidate is in a blocked library; an older accessible movie exists. With
        // limit=1, the accessible item must fill the slot — the blocked item is filtered before
        // slots are consumed (WatchlistController's limit-after-ACL rule), not after.
        var allowedLib = new Library { Id = Guid.NewGuid(), Name = "Movies B", Type = LibraryType.Movie };
        var allowed = new MediaItem
        {
            Id = Guid.NewGuid(), LibraryId = allowedLib.Id, Type = MediaType.Movie,
            Title = "Visible", Duration = 3600,
        };
        using (var ctx = new AppDbContext(_options))
        {
            ctx.Libraries.Add(allowedLib);
            ctx.MediaItems.Add(allowed);
            ctx.SaveChanges();
        }
        var blocked = AddMovie("Hidden Newest"); // lives in _movieLib, which the ACL below blocks
        AddInteraction(blocked.Id, position: 500, DateTime.UtcNow);
        AddInteraction(allowed.Id, position: 500, DateTime.UtcNow.AddDays(-1));
        _access.Setup(a => a.GetCurrentAsync())
            .ReturnsAsync(LibraryAccess.AllowOnly(new[] { allowedLib.Id, _tvLib.Id }));

        var row = await Build().GetContinueWatchingAsync(_userId, 1);

        var entry = Assert.Single(row);
        Assert.Equal(allowed.Id, entry.Id);
    }

    [Fact]
    public async Task In_progress_item_beyond_the_first_candidate_page_is_still_found()
    {
        // 301 newer watched-episode rows (one fully-watched series) would have pushed the movie
        // out of a single fixed 300-row scan window; paging must reach it.
        var (series, _) = AddSeriesWithEpisode("Marathon");
        var movie = AddMovie("Old In-Progress");
        AddInteraction(movie.Id, position: 900, DateTime.UtcNow.AddDays(-30));

        var episodes = new List<MediaItem>();
        for (var n = 2; n <= 302; n++)
        {
            episodes.Add(new MediaItem
            {
                Id = Guid.NewGuid(), LibraryId = _tvLib.Id, Type = MediaType.Episode,
                Title = $"Marathon E{n}", SeriesId = series.Id, SeasonNumber = 1, EpisodeNumber = n, Duration = 1500,
            });
        }
        using (var ctx = new AppDbContext(_options))
        {
            ctx.MediaItems.AddRange(episodes);
            var baseTime = DateTime.UtcNow.AddDays(-1);
            ctx.UserMediaInteractions.AddRange(episodes.Select((ep, i) => new UserMediaInteraction
            {
                UserId = _userId, MediaItemId = ep.Id,
                IsWatched = true, PlaybackPosition = 0, LastPlayed = baseTime.AddMinutes(i),
            }));
            ctx.SaveChanges();
        }
        SetupResolver(series.Id, episodes[0].Id, resumePosition: 0, seriesComplete: true);

        var row = await Build().GetContinueWatchingAsync(_userId, 20);

        var entry = Assert.Single(row); // series resolves complete; only the movie remains
        Assert.Equal(movie.Id, entry.Id);
        _recommendations.Verify(r => r.GetNextEpisodeAsync(_userId, series.Id), Times.Once);
    }

    [Fact]
    public async Task Limit_caps_the_row_at_the_newest_entries()
    {
        var m1 = AddMovie("One");
        var m2 = AddMovie("Two");
        var m3 = AddMovie("Three");
        AddInteraction(m1.Id, 100, DateTime.UtcNow.AddDays(-3));
        AddInteraction(m2.Id, 100, DateTime.UtcNow.AddDays(-2));
        AddInteraction(m3.Id, 100, DateTime.UtcNow.AddDays(-1));

        var row = await Build().GetContinueWatchingAsync(_userId, 2);

        Assert.Equal(new[] { m3.Id, m2.Id }, row.Select(r => r.Id).ToArray());
    }
}
