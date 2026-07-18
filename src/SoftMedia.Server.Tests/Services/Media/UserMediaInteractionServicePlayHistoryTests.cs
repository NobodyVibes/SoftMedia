using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SoftMedia.Server.Data;
using SoftMedia.Server.Models;
using SoftMedia.Server.Services.Media;
using SoftMedia.Server.Services.Security.ContentRating;
using SoftMedia.Server.Services.Security.LibraryAccess;
using Xunit;

namespace SoftMedia.Server.Tests.Services.Media;

/// R-WI-013 — per-play history recorded inside the progress-beat flow. Pins the §7 Q5 proposed
/// defaults: play threshold min(240s video / 60s audio, 50% of runtime), 6h dedup window,
/// completion via MediaCompletionHelper, PlayCount/LastPlayed made real, and the guards
/// (AV types only, position<=0 reset beats ignored).
public class UserMediaInteractionServicePlayHistoryTests
{
    private readonly AppDbContext _ctx;
    private readonly UserMediaInteractionService _svc;
    private readonly Guid _user = Guid.NewGuid();
    private readonly Guid _movie = Guid.NewGuid();   // 3600s
    private readonly Guid _track = Guid.NewGuid();   // 180s audio
    private readonly Guid _jingle = Guid.NewGuid();  // 40s audio
    private readonly Guid _book = Guid.NewGuid();
    private readonly Guid _movieLib = Guid.NewGuid();
    private readonly Guid _musicLib = Guid.NewGuid();

    public UserMediaInteractionServicePlayHistoryTests()
    {
        _ctx = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"play-history-{Guid.NewGuid()}").Options);
        _ctx.MediaItems.AddRange(
            new MediaItem { Id = _movie, Title = "Movie", Type = MediaType.Movie, Duration = 3600, Path = "m", LibraryId = _movieLib },
            new MediaItem { Id = _track, Title = "Track", Type = MediaType.Audio, Duration = 180, Path = "t", LibraryId = _musicLib },
            new MediaItem { Id = _jingle, Title = "Jingle", Type = MediaType.Audio, Duration = 40, Path = "j", LibraryId = _musicLib },
            new MediaItem { Id = _book, Title = "Book", Type = MediaType.Book, Duration = 0, Path = "b", LibraryId = _movieLib });
        // The recording guard is fail-closed for unknown users (privacy), so the beat-sending
        // user must exist like every real request's user does.
        _ctx.Users.Add(new User { Id = _user, Username = "player", PasswordHash = "x" });
        _ctx.SaveChanges();
        _svc = new UserMediaInteractionService(_ctx, NullLogger<UserMediaInteractionService>.Instance);
    }

    // ---- Threshold rule (pure) ----

    [Theory]
    [InlineData(MediaType.Movie, 240, 3600, true)]   // long movie: 240s absolute wins
    [InlineData(MediaType.Movie, 239, 3600, false)]
    [InlineData(MediaType.Movie, 45, 90, true)]      // short clip: 50% of 90s = 45s wins
    [InlineData(MediaType.Movie, 44, 90, false)]
    [InlineData(MediaType.Audio, 60, 180, true)]     // song: 60s absolute wins
    [InlineData(MediaType.Audio, 59, 180, false)]
    [InlineData(MediaType.Audio, 20, 40, true)]      // jingle: 50% of 40s = 20s wins
    [InlineData(MediaType.Movie, 240, 0, true)]      // unknown duration → absolute only
    [InlineData(MediaType.Movie, 239, 0, false)]
    public void CrossesPlayThreshold_MinOfAbsoluteAndHalfRuntime(MediaType type, double pos, double dur, bool expected)
        => Assert.Equal(expected, UserMediaInteractionService.CrossesPlayThreshold(type, pos, dur));

    // ---- Recording behaviour ----

    [Fact]
    public async Task BeatBelowThreshold_UpdatesProgress_ButOpensNoPlay()
    {
        await _svc.UpdateProgressAsync(_user, _movie, 120);

        Assert.Empty(_ctx.PlaybackHistory);
        var interaction = await _ctx.UserMediaInteractions.SingleAsync();
        Assert.Equal(120, interaction.PlaybackPosition); // resume state unaffected by the threshold
        Assert.Equal(0, (await _ctx.MediaItems.FindAsync(_movie))!.PlayCount);
    }

    [Fact]
    public async Task BeatCrossingThreshold_OpensPlay_AndMakesPlayCountReal()
    {
        await _svc.UpdateProgressAsync(_user, _movie, 250);

        var row = Assert.Single(_ctx.PlaybackHistory.ToList());
        Assert.Equal(_user, row.UserId);
        Assert.Equal(MediaType.Movie, row.MediaType);
        Assert.Equal(250, row.MaxPosition);
        Assert.False(row.Completed);

        var item = (await _ctx.MediaItems.FindAsync(_movie))!;
        Assert.Equal(1, item.PlayCount);      // the dead column lives
        Assert.NotNull(item.LastPlayed);
    }

    [Fact]
    public async Task SubsequentBeats_ContinueTheSamePlay()
    {
        await _svc.UpdateProgressAsync(_user, _movie, 250);
        await _svc.UpdateProgressAsync(_user, _movie, 600);
        await _svc.UpdateProgressAsync(_user, _movie, 400); // seek back — MaxPosition keeps the high-water mark

        var row = Assert.Single(_ctx.PlaybackHistory.ToList());
        Assert.Equal(600, row.MaxPosition);
        Assert.Equal(1, (await _ctx.MediaItems.FindAsync(_movie))!.PlayCount); // still one play
    }

    [Fact]
    public async Task BeatPast95Percent_CompletesThePlay()
    {
        await _svc.UpdateProgressAsync(_user, _movie, 250);
        await _svc.UpdateProgressAsync(_user, _movie, 3600 * 0.96);

        Assert.True(_ctx.PlaybackHistory.Single().Completed);
    }

    [Fact]
    public async Task ContinuedBeatsPastCompletion_StayOnePlay_NoPhantomRows()
    {
        // Review HIGH: a movie watched straight through keeps beating every ~10s through the
        // tail after the 95%/credits completion point. Those tail beats must NOT each open a
        // new completed row (the pre-fix bug turned one viewing into ~18 plays).
        await _svc.UpdateProgressAsync(_user, _movie, 250);   // opens the play
        await _svc.UpdateProgressAsync(_user, _movie, 3420);  // crosses 95% (3420/3600) -> completes
        Assert.True(_ctx.PlaybackHistory.Single().Completed);

        foreach (var pos in new[] { 3430.0, 3450, 3500, 3550, 3580, 3599 }) // credits/tail beats
            await _svc.UpdateProgressAsync(_user, _movie, pos);

        Assert.Single(_ctx.PlaybackHistory.ToList());                          // still exactly ONE play
        Assert.Equal(1, (await _ctx.MediaItems.FindAsync(_movie))!.PlayCount); // not ~18
        Assert.Equal(3599, _ctx.PlaybackHistory.Single().MaxPosition);         // high-water advanced
    }

    [Fact]
    public async Task ScrubBackNearEndAfterCompletion_StaysOnePlay()
    {
        // A scrub back to a still-high position (>= 50% of the reached high-water mark) is the
        // same viewing, not a rewatch — must not spawn a play.
        await _svc.UpdateProgressAsync(_user, _movie, 3420); // completes, MaxPosition 3420
        await _svc.UpdateProgressAsync(_user, _movie, 3000); // scrub to 83% (>= 1710) -> continues
        await _svc.UpdateProgressAsync(_user, _movie, 2000); // scrub to 56% (>= 1710) -> continues

        Assert.Single(_ctx.PlaybackHistory.ToList());
        Assert.Equal(1, (await _ctx.MediaItems.FindAsync(_movie))!.PlayCount);
    }

    [Fact]
    public async Task RewatchAfterCompletion_CountsASecondPlay()
    {
        await _svc.UpdateProgressAsync(_user, _movie, 3600 * 0.96); // one beat straight past completion
        Assert.True(_ctx.PlaybackHistory.Single().Completed);

        await _svc.UpdateProgressAsync(_user, _movie, 100);  // below threshold: no new row yet
        Assert.Single(_ctx.PlaybackHistory.ToList());

        await _svc.UpdateProgressAsync(_user, _movie, 260);  // crossed: rewatch recorded
        Assert.Equal(2, _ctx.PlaybackHistory.Count());
        Assert.Equal(2, (await _ctx.MediaItems.FindAsync(_movie))!.PlayCount);
    }

    [Fact]
    public async Task BeatAfterTheSessionWindow_OpensANewPlay()
    {
        await _svc.UpdateProgressAsync(_user, _movie, 250);
        var row = _ctx.PlaybackHistory.Single();
        row.LastBeatAt = DateTime.UtcNow - TimeSpan.FromHours(7); // simulate an old, abandoned play
        await _ctx.SaveChangesAsync();

        await _svc.UpdateProgressAsync(_user, _movie, 500);

        Assert.Equal(2, _ctx.PlaybackHistory.Count());
    }

    [Fact]
    public async Task BookBeats_AndPositionZeroResets_NeverRecordPlays()
    {
        await _svc.UpdateProgressAsync(_user, _book, 500, bookLocation: "cfi(/6/4)"); // page-turn
        await _svc.UpdateProgressAsync(_user, _movie, 0);                             // next-episode reset

        Assert.Empty(_ctx.PlaybackHistory);
        Assert.Equal(2, _ctx.UserMediaInteractions.Count()); // progress itself still stored
    }

    [Fact]
    public async Task AudioBeats_UseTheAudioThreshold()
    {
        await _svc.UpdateProgressAsync(_user, _track, 59);   // 180s song: below min(60, 90)
        Assert.Empty(_ctx.PlaybackHistory);

        await _svc.UpdateProgressAsync(_user, _track, 61);   // crossed
        await _svc.UpdateProgressAsync(_user, _jingle, 21);  // 40s jingle: min(60, 20) = 20

        Assert.Equal(2, _ctx.PlaybackHistory.Count());
    }

    [Fact]
    public async Task ExplicitMarkWatched_CompletesTheOpenPlay()
    {
        await _svc.UpdateProgressAsync(_user, _movie, 3000); // open, not yet complete (< 95%)
        Assert.False(_ctx.PlaybackHistory.Single().Completed);

        await _svc.MarkWatchedAsync(_user, _movie, watched: true); // next-episode overlay path

        Assert.True(_ctx.PlaybackHistory.Single().Completed);
    }

    [Fact]
    public async Task History_IsSelfScoped_NewestFirst_AndPaged()
    {
        var otherUser = Guid.NewGuid();
        _ctx.Users.Add(new User { Id = otherUser, Username = "other2", PasswordHash = "x" });
        await _ctx.SaveChangesAsync();
        await _svc.UpdateProgressAsync(_user, _movie, 250);
        await _svc.UpdateProgressAsync(_user, _track, 61);
        await _svc.UpdateProgressAsync(otherUser, _movie, 250);

        var mine = await _svc.GetHistoryAsync(_user, page: 1, pageSize: 10, LibraryAccess.Unrestricted, UserRatingCeilings.Unrestricted);
        Assert.Equal(2, mine.Count);
        Assert.All(mine, h => Assert.Equal(_user, h.UserId));           // never anyone else's rows
        Assert.True(mine[0].LastBeatAt >= mine[1].LastBeatAt);          // newest first
        Assert.NotNull(mine[0].MediaItem);                              // joined for titles

        var page2 = await _svc.GetHistoryAsync(_user, page: 2, pageSize: 1, LibraryAccess.Unrestricted, UserRatingCeilings.Unrestricted);
        Assert.Single(page2);
    }

    [Fact]
    public async Task History_HidesPlays_ForLibrariesTheUserCanNoLongerAccess()
    {
        // Review finding: history must not leak titles of media the user lost access to. The
        // movie is in a library the user is no longer granted; only the track's library remains.
        await _svc.UpdateProgressAsync(_user, _movie, 250);
        await _svc.UpdateProgressAsync(_user, _track, 61);

        var restricted = LibraryAccess.AllowOnly(new[] { _musicLib }); // movie's library revoked
        var visible = await _svc.GetHistoryAsync(_user, 1, 50, restricted, UserRatingCeilings.Unrestricted);

        Assert.Single(visible);
        Assert.Equal(_track, visible[0].MediaItemId);                   // the accessible one only
        Assert.DoesNotContain(visible, h => h.MediaItemId == _movie);   // revoked-library title hidden
        Assert.Equal(2, _ctx.PlaybackHistory.Count());                  // both plays still recorded; read is filtered
    }

    // ---- Privacy follow-up: user-owned recording toggle + clear-my-history ----

    [Fact]
    public async Task HistoryOff_RecordsNothing_ButResumeStateStillWorks()
    {
        (await _ctx.Users.FindAsync(_user))!.RecordPlaybackHistory = false;
        await _ctx.SaveChangesAsync();

        await _svc.UpdateProgressAsync(_user, _movie, 250);   // would open a play if recording
        await _svc.UpdateProgressAsync(_user, _movie, 3420);  // would complete it

        Assert.Empty(_ctx.PlaybackHistory);                                     // no diary
        Assert.Equal(0, (await _ctx.MediaItems.FindAsync(_movie))!.PlayCount);  // no aggregate bump
        var resume = await _ctx.UserMediaInteractions.SingleAsync();
        Assert.Equal(3420, resume.PlaybackPosition);                            // resume unaffected
    }

    [Fact]
    public async Task TogglingHistoryBackOn_RecordsFromTheNextQualifyingBeat()
    {
        (await _ctx.Users.FindAsync(_user))!.RecordPlaybackHistory = false;
        await _ctx.SaveChangesAsync();

        await _svc.UpdateProgressAsync(_user, _movie, 250);
        Assert.Empty(_ctx.PlaybackHistory);

        await _svc.SetRecordHistoryAsync(_user, true);
        await _svc.UpdateProgressAsync(_user, _movie, 400);

        Assert.Single(_ctx.PlaybackHistory.ToList());
        Assert.True(await _svc.GetRecordHistoryAsync(_user));
    }

    [Fact]
    public void NewUser_DefaultsToRecording()
        => Assert.True(new User().RecordPlaybackHistory);

    [Fact]
    public async Task MarkWatchedWhileHistoryOff_LeavesTheDiaryUntouched()
    {
        // Privacy review (medium): the auto "watched" POST at episode end must not stamp
        // Completed/LastBeatAt onto an open diary row after the user opted out mid-viewing.
        await _svc.UpdateProgressAsync(_user, _movie, 250); // open play while recording ON
        var openedAt = _ctx.PlaybackHistory.Single().LastBeatAt;

        (await _ctx.Users.FindAsync(_user))!.RecordPlaybackHistory = false;
        await _ctx.SaveChangesAsync();

        await _svc.MarkWatchedAsync(_user, _movie, watched: true); // next-episode overlay path

        var row = _ctx.PlaybackHistory.Single();
        Assert.False(row.Completed);                 // no post-opt-out completion stamp
        Assert.Equal(openedAt, row.LastBeatAt);      // diary frozen at the opt-out point
        var interaction = await _ctx.UserMediaInteractions.SingleAsync();
        Assert.True(interaction.IsWatched);          // the functional watched flag still works
    }

    [Fact]
    public async Task ClearHistory_ErasesOnlyMyRows_AndRecomputesAggregates()
    {
        var otherUser = Guid.NewGuid();
        _ctx.Users.Add(new User { Id = otherUser, Username = "other", PasswordHash = "x" });
        await _ctx.SaveChangesAsync();
        // My plays: movie + track. Other user's play: the same movie (must survive).
        await _svc.UpdateProgressAsync(_user, _movie, 250);
        await _svc.UpdateProgressAsync(_user, _track, 61);
        await _svc.UpdateProgressAsync(otherUser, _movie, 250);
        Assert.Equal(2, (await _ctx.MediaItems.FindAsync(_movie))!.PlayCount);

        var deleted = await _svc.ClearHistoryAsync(_user);

        Assert.Equal(2, deleted);
        var survivors = _ctx.PlaybackHistory.ToList();
        Assert.Single(survivors);
        Assert.Equal(otherUser, survivors[0].UserId);                            // other user's diary intact
        Assert.Equal(1, (await _ctx.MediaItems.FindAsync(_movie))!.PlayCount);   // recomputed: 1 remaining row
        Assert.Equal(0, (await _ctx.MediaItems.FindAsync(_track))!.PlayCount);   // no rows left → 0
        Assert.Null((await _ctx.MediaItems.FindAsync(_track))!.LastPlayed);
        Assert.NotNull((await _ctx.MediaItems.FindAsync(_movie))!.LastPlayed);
    }

    [Fact]
    public async Task History_HidesPlays_AboveTheUsersCurrentRatingCeiling()
    {
        // The movie is rated R; a user later capped at PG must not see it in history.
        (await _ctx.MediaItems.FindAsync(_movie))!.ContentRating = "R";
        await _ctx.SaveChangesAsync();

        await _svc.UpdateProgressAsync(_user, _movie, 250);
        await _svc.UpdateProgressAsync(_user, _track, 61);

        var pgCeiling = UserRatingCeilings.From(new User { MaxRating = "PG", ContentRatings = "{}" });
        var visible = await _svc.GetHistoryAsync(_user, 1, 50, LibraryAccess.Unrestricted, pgCeiling);

        Assert.DoesNotContain(visible, h => h.MediaItemId == _movie);   // R movie hidden under PG cap
        Assert.Contains(visible, h => h.MediaItemId == _track);         // unrated track still shows
    }
}
