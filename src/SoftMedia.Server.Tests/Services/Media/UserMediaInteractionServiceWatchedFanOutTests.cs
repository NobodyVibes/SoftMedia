using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SoftMedia.Server.Data;
using SoftMedia.Server.Models;
using SoftMedia.Server.Services.Media;
using Xunit;

namespace SoftMedia.Server.Tests.Services.Media;

/// <summary>
/// DV-WI-005 — watched is a property of the EPISODE, not the file row. Duplicate files
/// of one episode share (SeriesId, Season, Episode); MarkWatchedAsync fans the flag out
/// to sibling rows (both directions) so the other copy never disagrees. Only IsWatched
/// (plus a resume-point reset on watch) propagates — LastPlayed and play history stay
/// per-file, and movies are untouched until VersionGroupId exists (plan DV-WI-014).
/// </summary>
public class UserMediaInteractionServiceWatchedFanOutTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;
    private readonly Guid _userId = Guid.NewGuid();
    private readonly Guid _seriesId = Guid.NewGuid();
    private readonly Guid _libraryId = Guid.NewGuid();

    public UserMediaInteractionServiceWatchedFanOutTests()
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
        ctx.Libraries.Add(new Library { Id = _libraryId, Name = "TV", Type = LibraryType.TV });
        ctx.MediaItems.Add(new MediaItem { Id = _seriesId, LibraryId = _libraryId, Type = MediaType.Series, Title = "Show" });
        ctx.SaveChanges();
    }

    public void Dispose() => _connection.Dispose();

    private MediaItem AddEpisode(int season, int episode, string title)
    {
        var ep = new MediaItem
        {
            Id = Guid.NewGuid(), LibraryId = _libraryId, Type = MediaType.Episode,
            Title = title, SeriesId = _seriesId, SeasonNumber = season, EpisodeNumber = episode, Duration = 1000,
        };
        using var ctx = new AppDbContext(_options);
        ctx.MediaItems.Add(ep);
        ctx.SaveChanges();
        return ep;
    }

    private MediaItem AddMovie(string title)
    {
        var m = new MediaItem
        {
            Id = Guid.NewGuid(), LibraryId = _libraryId, Type = MediaType.Movie,
            Title = title, Duration = 5000,
        };
        using var ctx = new AppDbContext(_options);
        ctx.MediaItems.Add(m);
        ctx.SaveChanges();
        return m;
    }

    private void AddInteraction(Guid mediaId, bool watched, double? position = null)
    {
        using var ctx = new AppDbContext(_options);
        ctx.UserMediaInteractions.Add(new UserMediaInteraction
        {
            UserId = _userId, MediaItemId = mediaId, IsWatched = watched, PlaybackPosition = position,
        });
        ctx.SaveChanges();
    }

    private UserMediaInteractionService Build(out AppDbContext ctx)
    {
        ctx = new AppDbContext(_options);
        return new UserMediaInteractionService(ctx, NullLogger<UserMediaInteractionService>.Instance);
    }

    private UserMediaInteraction? Interaction(Guid mediaId)
    {
        using var ctx = new AppDbContext(_options);
        return ctx.UserMediaInteractions.AsNoTracking()
            .FirstOrDefault(i => i.UserId == _userId && i.MediaItemId == mediaId);
    }

    [Fact]
    public async Task MarkWatched_FansOutToSiblingCopies_AndOnlyToThem()
    {
        var copyA = AddEpisode(1, 3, "E3 (1080p)");
        var copyB = AddEpisode(1, 3, "E3 (4K)");
        var other = AddEpisode(1, 4, "E4");

        var service = Build(out var ctx);
        await using (ctx) await service.MarkWatchedAsync(_userId, copyA.Id, watched: true);

        Assert.True(Interaction(copyA.Id)!.IsWatched);
        Assert.True(Interaction(copyB.Id)!.IsWatched);   // sibling row created + flagged
        Assert.Null(Interaction(other.Id));              // E4 untouched
    }

    [Fact]
    public async Task MarkWatched_ClearsTheSiblingsStaleResumePoint()
    {
        var copyA = AddEpisode(1, 3, "E3 (1080p)");
        var copyB = AddEpisode(1, 3, "E3 (4K)");
        AddInteraction(copyB.Id, watched: false, position: 480); // half-watched other copy

        var service = Build(out var ctx);
        await using (ctx) await service.MarkWatchedAsync(_userId, copyA.Id, watched: true);

        var sibling = Interaction(copyB.Id)!;
        Assert.True(sibling.IsWatched);
        Assert.Equal(0, sibling.PlaybackPosition); // the episode is finished; no dangling resume
    }

    [Fact]
    public async Task Unwatch_FansOutToSiblings_WithoutCreatingRowsForUntouchedCopies()
    {
        var copyA = AddEpisode(1, 3, "E3 (1080p)");
        var copyB = AddEpisode(1, 3, "E3 (4K)");
        var copyC = AddEpisode(1, 3, "E3 (720p)"); // never interacted with
        AddInteraction(copyA.Id, watched: true);
        AddInteraction(copyB.Id, watched: true);

        var service = Build(out var ctx);
        await using (ctx) await service.MarkWatchedAsync(_userId, copyA.Id, watched: false);

        Assert.False(Interaction(copyA.Id)!.IsWatched);
        // The sibling's row carried nothing but the flag — cleared AND garbage-collected
        // (DV-WI-014 emptiness rule); either way it reads unwatched.
        Assert.Null(Interaction(copyB.Id));
        Assert.Null(Interaction(copyC.Id)); // unwatching must not mint empty rows
    }

    [Fact]
    public async Task MarkWatched_UngroupedMovie_DoesNotFanOutOnTitleAlone()
    {
        // A bare title collision (no VersionGroupId) must not leak watched state across
        // genuinely different items — only the group id fans movies out.
        var copyA = AddMovie("Inception");
        var copyB = AddMovie("Inception");

        var service = Build(out var ctx);
        await using (ctx) await service.MarkWatchedAsync(_userId, copyA.Id, watched: true);

        Assert.True(Interaction(copyA.Id)!.IsWatched);
        Assert.Null(Interaction(copyB.Id));
    }

    // ─────────────── DV-WI-014: group-keyed fan-out (movies + all title-level state) ───────────────

    private (MediaItem A, MediaItem B) AddMovieGroup(string title)
    {
        var group = Guid.NewGuid();
        var a = AddMovie(title);
        var b = AddMovie(title);
        using var ctx = new AppDbContext(_options);
        foreach (var id in new[] { a.Id, b.Id })
            ctx.MediaItems.Find(id)!.VersionGroupId = group;
        ctx.SaveChanges();
        return (a, b);
    }

    [Fact]
    public async Task MarkWatched_GroupedMovie_FansOutViaVersionGroup()
    {
        var (a, b) = AddMovieGroup("Inception");

        var service = Build(out var ctx);
        await using (ctx) await service.MarkWatchedAsync(_userId, a.Id, watched: true);

        Assert.True(Interaction(a.Id)!.IsWatched);
        Assert.True(Interaction(b.Id)!.IsWatched);
    }

    [Fact]
    public async Task Rating_FansOutToSiblings_AndRecomputesBothInternalAverages()
    {
        var (a, b) = AddMovieGroup("Inception");

        var service = Build(out var ctx);
        await using (ctx) await service.RateMediaAsync(_userId, a.Id, 8);

        Assert.Equal(8, Interaction(a.Id)!.Rating);
        Assert.Equal(8, Interaction(b.Id)!.Rating);
        using var verify = new AppDbContext(_options);
        Assert.Equal(8, verify.MediaItems.Find(a.Id)!.InternalRating);
        Assert.Equal(8, verify.MediaItems.Find(b.Id)!.InternalRating); // sibling's average absorbed the fan-out
    }

    [Fact]
    public async Task ClearingRating_FansOut_AndGarbageCollectsEmptySiblingRows()
    {
        var (a, b) = AddMovieGroup("Inception");

        var service = Build(out var ctx);
        await using (ctx)
        {
            await service.RateMediaAsync(_userId, a.Id, 8);
            await service.RateMediaAsync(_userId, a.Id, null);
        }

        Assert.Null(Interaction(a.Id)); // primary row GC'd (pre-existing rule)
        Assert.Null(Interaction(b.Id)); // sibling row GC'd too — no dead state
    }

    // ─────────────── DV-WI-024: play history across a mid-sitting version switch ───────────────

    private void EnableHistoryRecording()
    {
        using var ctx = new AppDbContext(_options);
        ctx.Users.Find(_userId)!.RecordPlaybackHistory = true;
        ctx.SaveChanges();
    }

    [Fact]
    public async Task VersionSwitch_MidSitting_KeepsOnePlay_AndMovesThePlayCountWithIt()
    {
        var (a, b) = AddMovieGroup("Inception");
        EnableHistoryRecording();

        var serviceA = Build(out var ctxA);
        await using (ctxA) await serviceA.UpdateProgressAsync(_userId, a.Id, 1500); // opens the play on copy A
        var serviceB = Build(out var ctxB);
        await using (ctxB) await serviceB.UpdateProgressAsync(_userId, b.Id, 1600); // switched to copy B

        using var verify = new AppDbContext(_options);
        var play = Assert.Single(verify.PlaybackHistory.ToList()); // ONE sitting, ONE play
        Assert.Equal(b.Id, play.MediaItemId);                      // re-keyed to the new file
        Assert.Equal(1600, play.MaxPosition);
        Assert.Equal(0, verify.MediaItems.Find(a.Id)!.PlayCount);  // count moved with the row
        Assert.Equal(1, verify.MediaItems.Find(b.Id)!.PlayCount);
    }

    [Fact]
    public async Task VersionSwitch_RestartingFromTheTop_IsItsOwnPlay()
    {
        // An edition switched from 0 (position fell below half the play's high-water
        // mark) is a fresh viewing, not a continuation — same rule as same-item rewinds.
        var (a, b) = AddMovieGroup("Inception");
        EnableHistoryRecording();

        var serviceA = Build(out var ctxA);
        await using (ctxA) await serviceA.UpdateProgressAsync(_userId, a.Id, 1500);
        var serviceB = Build(out var ctxB);
        await using (ctxB) await serviceB.UpdateProgressAsync(_userId, b.Id, 300); // restart, past threshold

        using var verify = new AppDbContext(_options);
        Assert.Equal(2, verify.PlaybackHistory.Count());
        Assert.Equal(1, verify.MediaItems.Find(a.Id)!.PlayCount);
        Assert.Equal(1, verify.MediaItems.Find(b.Id)!.PlayCount);
    }

    [Fact]
    public async Task FavoriteAndWatchlist_FanOutToSiblings_WithSharedTimestamp()
    {
        var (a, b) = AddMovieGroup("Inception");

        var service = Build(out var ctx);
        await using (ctx)
        {
            await service.ToggleFavoriteAsync(_userId, a.Id, true);
            await service.ToggleWatchlistAsync(_userId, a.Id, true);
        }

        var siblingRow = Interaction(b.Id)!;
        Assert.True(siblingRow.IsFavorite);
        Assert.True(siblingRow.IsWatchlisted);
        Assert.Equal(Interaction(a.Id)!.WatchlistedAt, siblingRow.WatchlistedAt); // one add, one sort position
    }
}
