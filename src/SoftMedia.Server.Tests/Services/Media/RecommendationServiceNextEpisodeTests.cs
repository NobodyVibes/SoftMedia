using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SoftMedia.Server.Data;
using SoftMedia.Server.Models;
using SoftMedia.Server.Services.Infrastructure;
using SoftMedia.Server.Services.Media;
using SoftMedia.Server.Services.Security.ContentRating;
using SoftMedia.Server.Services.Security.LibraryAccess;
using Xunit;

namespace SoftMedia.Server.Tests.Services.Media;

/// <summary>
/// The next-episode resolver against the REAL repositories (the ContinueWatchingService tests mock
/// it, so its semantics need their own guard). The load-bearing contract for the Continue Watching
/// row: <c>IsSeriesComplete</c> means EVERY episode is finished — never merely "the most recently
/// played episode was the last one in order" — and after a finished episode the resolver offers the
/// first UNFINISHED episode (skipping watched ones, wrapping to the start of the series).
/// </summary>
public class RecommendationServiceNextEpisodeTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;
    private readonly Guid _userId = Guid.NewGuid();
    private readonly Guid _seriesId = Guid.NewGuid();
    private readonly List<MediaItem> _episodes = new();

    public RecommendationServiceNextEpisodeTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;

        using var ctx = new AppDbContext(_options);
        ctx.Database.EnsureCreated();

        var lib = new Library { Id = Guid.NewGuid(), Name = "TV", Type = LibraryType.TV };
        var series = new MediaItem { Id = _seriesId, LibraryId = lib.Id, Type = MediaType.Series, Title = "Show" };
        ctx.Users.Add(new User
        {
            Id = _userId, Username = "viewer", PasswordHash = "x", Role = UserRole.User,
            IsApproved = true, CreatedAt = DateTime.UtcNow, FirstName = "T", LastName = "T", ContentRatings = "{}",
        });
        ctx.Libraries.Add(lib);
        ctx.MediaItems.Add(series);
        for (var n = 1; n <= 3; n++)
        {
            var ep = new MediaItem
            {
                Id = Guid.NewGuid(), LibraryId = lib.Id, Type = MediaType.Episode,
                Title = $"E{n}", SeriesId = _seriesId, SeasonNumber = 1, EpisodeNumber = n, Duration = 1000,
            };
            _episodes.Add(ep);
            ctx.MediaItems.Add(ep);
        }
        ctx.SaveChanges();
    }

    public void Dispose() => _connection.Dispose();

    private RecommendationService Build()
    {
        var ctx = new AppDbContext(_options);
        var ratings = new Mock<IUserContentRatingProvider>();
        ratings.Setup(r => r.GetCurrentAsync()).ReturnsAsync(UserRatingCeilings.Unrestricted);
        var access = new Mock<IUserLibraryAccessProvider>();
        access.Setup(a => a.GetCurrentAsync()).ReturnsAsync(LibraryAccess.Unrestricted);

        return new RecommendationService(
            new MediaRepository(ctx, ratings.Object, access.Object),
            new UserMediaInteractionRepository(ctx),
            ctx,
            access.Object,
            ratings.Object,
            NullLogger<RecommendationService>.Instance);
    }

    private void AddInteraction(MediaItem episode, DateTime lastPlayed, bool watched, double? position = null)
    {
        using var ctx = new AppDbContext(_options);
        ctx.UserMediaInteractions.Add(new UserMediaInteraction
        {
            UserId = _userId, MediaItemId = episode.Id,
            IsWatched = watched, PlaybackPosition = position, LastPlayed = lastPlayed,
        });
        ctx.SaveChanges();
    }

    [Fact]
    public async Task Fully_watched_series_is_complete_even_when_newest_interaction_is_mid_series()
    {
        // The user finished everything, then rewatched E2 LAST (its LastPlayed is newest, position
        // reset by the watched-mark). A naive "next in sequence" would offer already-watched E3 and
        // keep the finished show in Continue Watching forever.
        var t = DateTime.UtcNow;
        AddInteraction(_episodes[0], t.AddDays(-3), watched: true);
        AddInteraction(_episodes[2], t.AddDays(-2), watched: true);
        AddInteraction(_episodes[1], t, watched: true); // E2 rewatched most recently

        var next = await Build().GetNextEpisodeAsync(_userId, _seriesId);

        Assert.NotNull(next);
        Assert.True(next!.IsSeriesComplete);
    }

    [Fact]
    public async Task Watching_only_the_finale_offers_the_first_unfinished_episode_not_series_complete()
    {
        // Only the last-in-order episode is watched — the series is NOT complete; the resolver
        // wraps to the earliest unfinished episode (E1).
        AddInteraction(_episodes[2], DateTime.UtcNow, watched: true);

        var next = await Build().GetNextEpisodeAsync(_userId, _seriesId);

        Assert.NotNull(next);
        Assert.False(next!.IsSeriesComplete);
        Assert.Equal(_episodes[0].Id, next.EpisodeId);
    }

    [Fact]
    public async Task Finished_episode_advances_to_the_next_unwatched_skipping_already_watched_ones()
    {
        // E1 finished most recently, E2 was already watched earlier — the resolver must skip E2
        // and offer E3, never an episode the user has already seen.
        var t = DateTime.UtcNow;
        AddInteraction(_episodes[1], t.AddDays(-1), watched: true);
        AddInteraction(_episodes[0], t, watched: true);

        var next = await Build().GetNextEpisodeAsync(_userId, _seriesId);

        Assert.NotNull(next);
        Assert.False(next!.IsSeriesComplete);
        Assert.Equal(_episodes[2].Id, next.EpisodeId);
    }

    [Fact]
    public async Task In_progress_episode_is_resumed_at_its_saved_position()
    {
        AddInteraction(_episodes[1], DateTime.UtcNow, watched: false, position: 250);

        var next = await Build().GetNextEpisodeAsync(_userId, _seriesId);

        Assert.NotNull(next);
        Assert.False(next!.IsSeriesComplete);
        Assert.Equal(_episodes[1].Id, next.EpisodeId);
        Assert.Equal(250, next.ResumePosition);
    }

    // ---- DV-WI-001/002: duplicate files of the same episode (quality/language variants) ----

    /// <summary>Adds a second (or third…) file for an existing episode number.</summary>
    private MediaItem AddEpisode(int season, int episode, string title, Guid? id = null)
    {
        var ep = new MediaItem
        {
            Id = id ?? Guid.NewGuid(), LibraryId = _episodes[0].LibraryId, Type = MediaType.Episode,
            Title = title, SeriesId = _seriesId, SeasonNumber = season, EpisodeNumber = episode, Duration = 1000,
        };
        using var ctx = new AppDbContext(_options);
        ctx.MediaItems.Add(ep);
        ctx.SaveChanges();
        _episodes.Add(ep);
        return ep;
    }

    [Fact]
    public async Task Autoplay_from_either_copy_of_a_duplicated_episode_advances_to_the_next_episode()
    {
        // DV-WI-001: two files of S01E02 sit adjacent in the ordering. "Next" from either
        // copy must be E3 — never the sibling copy of the episode the user just finished.
        var dupE2 = AddEpisode(1, 2, "E2 (4K)");

        var fromOriginal = await Build().GetNextEpisodeFromCurrentAsync(_userId, _episodes[1].Id);
        var fromDuplicate = await Build().GetNextEpisodeFromCurrentAsync(_userId, dupE2.Id);

        Assert.Equal(_episodes[2].Id, fromOriginal!.EpisodeId);
        Assert.Equal(_episodes[2].Id, fromDuplicate!.EpisodeId);
    }

    [Fact]
    public async Task Next_from_the_final_episode_reports_series_end_even_with_a_trailing_duplicate()
    {
        // A duplicate of the FINAL episode must not masquerade as a further episode.
        AddEpisode(1, 3, "E3 (4K)");

        var next = await Build().GetNextEpisodeFromCurrentAsync(_userId, _episodes[2].Id);

        Assert.Equal(Guid.Empty, next!.EpisodeId);
        Assert.True(next.IsSeriesComplete);
    }

    [Fact]
    public async Task Previous_navigation_skips_duplicates_and_lands_on_the_groups_first_copy()
    {
        // DV-WI-001: backward navigation from E3 must land on the SAME copy of E2 that
        // forward navigation would pick — the first row of the duplicate group in
        // (Season, Episode, Id) order. Fixed GUIDs pin that order deterministically.
        var first = AddEpisode(1, 2, "E2 (first copy)", Guid.Parse("00000000-0000-0000-0000-000000000001"));
        AddEpisode(1, 2, "E2 (second copy)", Guid.Parse("ffffffff-ffff-ffff-ffff-fffffffffffe"));

        var prev = await Build().GetPreviousEpisodeFromCurrentAsync(_userId, _episodes[2].Id);

        Assert.Equal(2, prev!.EpisodeNumber);
        Assert.Equal(first.Id, prev.EpisodeId);
    }

    [Fact]
    public async Task Series_with_a_duplicated_episode_completes_when_any_one_copy_is_watched()
    {
        // DV-WI-002: the never-played duplicate of E2 must not hold the series in
        // Continue Watching forever once E2 was watched via the other file.
        AddEpisode(1, 2, "E2 (4K)");
        var t = DateTime.UtcNow;
        AddInteraction(_episodes[0], t.AddDays(-2), watched: true);
        AddInteraction(_episodes[1], t.AddDays(-1), watched: true); // ONE copy of E2
        AddInteraction(_episodes[2], t, watched: true);

        var next = await Build().GetNextEpisodeAsync(_userId, _seriesId);

        Assert.True(next!.IsSeriesComplete);
    }

    [Fact]
    public async Task Duplicate_copy_of_a_watched_episode_is_never_offered_as_next()
    {
        // DV-WI-002: after finishing E2 (via one copy), the resolver must offer E3 —
        // not the untouched duplicate file of E2.
        AddEpisode(1, 2, "E2 (4K)");
        var t = DateTime.UtcNow;
        AddInteraction(_episodes[0], t.AddDays(-1), watched: true);
        AddInteraction(_episodes[1], t, watched: true);

        var next = await Build().GetNextEpisodeAsync(_userId, _seriesId);

        Assert.False(next!.IsSeriesComplete);
        Assert.Equal(_episodes[2].Id, next.EpisodeId);
    }
}
