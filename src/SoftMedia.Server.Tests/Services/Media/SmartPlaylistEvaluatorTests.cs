using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SoftMedia.Server.Data;
using SoftMedia.Server.Models;
using SoftMedia.Server.Services.Media;
using SoftMedia.Server.Services.Security.LibraryAccess;
using Xunit;

namespace SoftMedia.Server.Tests.Services.Media;

/// <summary>
/// Smart-playlist evaluation, deliberately against SQLITE rather than the EF
/// in-memory provider.
///
/// The evaluator leans on correlated subqueries over PlaybackHistory — inside
/// OrderBy, in the MostPlayed/RecentlyPlayed cases. The in-memory provider
/// happily evaluates any LINQ client-side, so it would pass these tests even if
/// the expression could not be translated to SQL, and the failure would only
/// appear as a 500 at runtime. Running on a real provider is the point.
/// </summary>
public class SmartPlaylistEvaluatorTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;
    private readonly Guid _owner = Guid.NewGuid();
    private readonly Guid _otherUser = Guid.NewGuid();
    private readonly Library _music;
    private readonly Library _blocked;

    private readonly MediaItem _old;
    private readonly MediaItem _fresh;
    private readonly MediaItem _favourite;
    private readonly MediaItem _inBlockedLibrary;

    public SmartPlaylistEvaluatorTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;

        _music = new Library { Id = Guid.NewGuid(), Name = "Music", Type = LibraryType.Music, Paths = new() { "/m" } };
        _blocked = new Library { Id = Guid.NewGuid(), Name = "Vinyl", Type = LibraryType.Music, Paths = new() { "/v" } };

        _old = Track(_music, "Old Song", addedDaysAgo: 400);
        _fresh = Track(_music, "Fresh Song", addedDaysAgo: 2);
        _favourite = Track(_music, "Beloved", addedDaysAgo: 50);
        _inBlockedLibrary = Track(_blocked, "Hidden", addedDaysAgo: 1);

        using var ctx = new AppDbContext(_options);
        ctx.Database.EnsureCreated();
        ctx.Libraries.AddRange(_music, _blocked);
        ctx.MediaItems.AddRange(_old, _fresh, _favourite, _inBlockedLibrary);
        ctx.Users.AddRange(
            new User { Id = _owner, Username = "owner", PasswordHash = "x", Role = UserRole.User, IsApproved = true },
            new User { Id = _otherUser, Username = "other", PasswordHash = "x", Role = UserRole.User, IsApproved = true });
        ctx.SaveChanges();
    }

    public void Dispose() => _connection.Dispose();

    private static MediaItem Track(Library lib, string title, int addedDaysAgo) => new()
    {
        Id = Guid.NewGuid(),
        LibraryId = lib.Id,
        Title = title,
        SortTitle = title,
        Path = $"/{title}.flac",
        Type = MediaType.Audio,
        DateAdded = DateTime.UtcNow.AddDays(-addedDaysAgo),
    };

    private async Task PlayAsync(AppDbContext ctx, Guid userId, MediaItem track, int times, DateTime? at = null)
    {
        for (var i = 0; i < times; i++)
        {
            ctx.PlaybackHistory.Add(new PlaybackHistory
            {
                UserId = userId,
                MediaItemId = track.Id,
                MediaType = MediaType.Audio,
                StartedAt = at ?? DateTime.UtcNow.AddDays(-i),
                LastBeatAt = at ?? DateTime.UtcNow.AddDays(-i),
                Completed = true,
            });
        }
        await ctx.SaveChangesAsync();
    }

    private SmartPlaylistEvaluator NewEvaluator(AppDbContext ctx) => new(ctx);

    // ── The privacy-critical case ────────────────────────────────────────────

    [Fact]
    public async Task MostPlayed_RanksByTheOwnersPlays_NotTheAllUserAggregate()
    {
        using var ctx = new AppDbContext(_options);

        // MediaItem.PlayCount is an all-user aggregate (see LibraryRepository). Set
        // it to say the opposite of the owner's own history: if the evaluator ever
        // reaches for it, "_old" wins and this test fails.
        var old = await ctx.MediaItems.FirstAsync(m => m.Id == _old.Id);
        old.PlayCount = 999;
        var fresh = await ctx.MediaItems.FirstAsync(m => m.Id == _fresh.Id);
        fresh.PlayCount = 0;
        await ctx.SaveChangesAsync();

        // The OWNER actually played "_fresh"; somebody else hammered "_old".
        await PlayAsync(ctx, _owner, _fresh, times: 3);
        await PlayAsync(ctx, _otherUser, _old, times: 50);

        var result = await NewEvaluator(ctx).EvaluateAsync(
            new SmartPlaylistRules { Sort = SmartPlaylistSort.MostPlayed },
            _owner, LibraryAccess.Unrestricted);

        Assert.Equal(_fresh.Id, result[0].Id);
    }

    [Fact]
    public async Task UnplayedOnly_IgnoresAnotherUsersPlays()
    {
        using var ctx = new AppDbContext(_options);
        // Another household member played it; for THIS owner it is still unplayed.
        await PlayAsync(ctx, _otherUser, _fresh, times: 5);

        var result = await NewEvaluator(ctx).EvaluateAsync(
            new SmartPlaylistRules { UnplayedOnly = true },
            _owner, LibraryAccess.Unrestricted);

        Assert.Contains(result, m => m.Id == _fresh.Id);
    }

    [Fact]
    public async Task UnplayedOnly_ExcludesTracksTheOwnerHasPlayed()
    {
        using var ctx = new AppDbContext(_options);
        await PlayAsync(ctx, _owner, _fresh, times: 1);

        var result = await NewEvaluator(ctx).EvaluateAsync(
            new SmartPlaylistRules { UnplayedOnly = true },
            _owner, LibraryAccess.Unrestricted);

        Assert.DoesNotContain(result, m => m.Id == _fresh.Id);
    }

    // ── Filters ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task FavoritesOnly_ReturnsOnlyTheOwnersFavourites()
    {
        using var ctx = new AppDbContext(_options);
        ctx.UserMediaInteractions.Add(new UserMediaInteraction
        {
            UserId = _owner, MediaItemId = _favourite.Id, IsFavorite = true,
        });
        // Another user's favourite must not leak into the owner's playlist.
        ctx.UserMediaInteractions.Add(new UserMediaInteraction
        {
            UserId = _otherUser, MediaItemId = _old.Id, IsFavorite = true,
        });
        await ctx.SaveChangesAsync();

        var result = await NewEvaluator(ctx).EvaluateAsync(
            new SmartPlaylistRules { FavoritesOnly = true },
            _owner, LibraryAccess.Unrestricted);

        Assert.Single(result);
        Assert.Equal(_favourite.Id, result[0].Id);
    }

    [Fact]
    public async Task AddedWithinDays_KeepsOnlyRecentAdditions()
    {
        using var ctx = new AppDbContext(_options);

        var result = await NewEvaluator(ctx).EvaluateAsync(
            new SmartPlaylistRules { AddedWithinDays = 30 },
            _owner, LibraryAccess.Unrestricted);

        Assert.Contains(result, m => m.Id == _fresh.Id);
        Assert.DoesNotContain(result, m => m.Id == _old.Id);
    }

    [Fact]
    public async Task Genre_KeepsOnlyTracksTaggedWithIt()
    {
        using var ctx = new AppDbContext(_options);
        var rock = new Genre { Name = "Rock" };
        var jazz = new Genre { Name = "Jazz" };
        ctx.Genres.AddRange(rock, jazz);
        await ctx.SaveChangesAsync();

        ctx.MediaItemGenres.AddRange(
            new MediaItemGenre { MediaItemId = _fresh.Id, GenreId = rock.Id },
            new MediaItemGenre { MediaItemId = _old.Id, GenreId = jazz.Id });
        await ctx.SaveChangesAsync();

        var result = await NewEvaluator(ctx).EvaluateAsync(
            new SmartPlaylistRules { Genre = "Rock" }, _owner, LibraryAccess.Unrestricted);

        Assert.Single(result);
        Assert.Equal(_fresh.Id, result[0].Id);
    }

    [Fact]
    public async Task Genre_IgnoresSurroundingWhitespace()
    {
        using var ctx = new AppDbContext(_options);
        var rock = new Genre { Name = "Rock" };
        ctx.Genres.Add(rock);
        await ctx.SaveChangesAsync();
        ctx.MediaItemGenres.Add(new MediaItemGenre { MediaItemId = _fresh.Id, GenreId = rock.Id });
        await ctx.SaveChangesAsync();

        var result = await NewEvaluator(ctx).EvaluateAsync(
            new SmartPlaylistRules { Genre = "  Rock  " }, _owner, LibraryAccess.Unrestricted);

        Assert.Single(result);
    }

    [Fact]
    public async Task Genre_MatchingNothingYieldsAnEmptyPlaylistRatherThanEverything()
    {
        using var ctx = new AppDbContext(_options);

        var result = await NewEvaluator(ctx).EvaluateAsync(
            new SmartPlaylistRules { Genre = "Polka" }, _owner, LibraryAccess.Unrestricted);

        Assert.Empty(result);
    }

    [Fact]
    public async Task ArtistId_KeepsOnlyThatArtistsTracks()
    {
        using var ctx = new AppDbContext(_options);
        var artist = new MediaItem
        {
            Id = Guid.NewGuid(), LibraryId = _music.Id, Title = "The Band", SortTitle = "The Band",
            Path = "/m/the-band", Type = MediaType.Artist,
        };
        ctx.MediaItems.Add(artist);
        var tagged = await ctx.MediaItems.FirstAsync(m => m.Id == _fresh.Id);
        tagged.ArtistId = artist.Id;
        await ctx.SaveChangesAsync();

        var result = await NewEvaluator(ctx).EvaluateAsync(
            new SmartPlaylistRules { ArtistId = artist.Id }, _owner, LibraryAccess.Unrestricted);

        Assert.Single(result);
        Assert.Equal(_fresh.Id, result[0].Id);
    }

    [Fact]
    public async Task Filters_CombineAsAnAnd()
    {
        using var ctx = new AppDbContext(_options);
        var rock = new Genre { Name = "Rock" };
        ctx.Genres.Add(rock);
        await ctx.SaveChangesAsync();

        // _old is Rock but was added 400 days ago, so the date window excludes it.
        ctx.MediaItemGenres.Add(new MediaItemGenre { MediaItemId = _old.Id, GenreId = rock.Id });
        await ctx.SaveChangesAsync();

        var result = await NewEvaluator(ctx).EvaluateAsync(
            new SmartPlaylistRules { Genre = "Rock", AddedWithinDays = 30 },
            _owner, LibraryAccess.Unrestricted);

        Assert.Empty(result);
    }

    [Fact]
    public async Task Evaluation_ExcludesLibrariesTheViewerIsDenied()
    {
        using var ctx = new AppDbContext(_options);

        var result = await NewEvaluator(ctx).EvaluateAsync(
            new SmartPlaylistRules(),
            _owner, LibraryAccess.AllowOnly(new[] { _music.Id }));

        Assert.DoesNotContain(result, m => m.Id == _inBlockedLibrary.Id);
    }

    [Fact]
    public async Task Evaluation_ExcludesNonAudioAndMissingFiles()
    {
        using var ctx = new AppDbContext(_options);
        ctx.MediaItems.Add(new MediaItem
        {
            Id = Guid.NewGuid(), LibraryId = _music.Id, Title = "A Movie", SortTitle = "A Movie",
            Path = "/m.mkv", Type = MediaType.Movie,
        });
        var gone = Track(_music, "Deleted", addedDaysAgo: 1);
        gone.IsMissing = true;
        ctx.MediaItems.Add(gone);
        await ctx.SaveChangesAsync();

        var result = await NewEvaluator(ctx).EvaluateAsync(
            new SmartPlaylistRules(), _owner, LibraryAccess.Unrestricted);

        Assert.All(result, m => Assert.Equal(MediaType.Audio, m.Type));
        Assert.DoesNotContain(result, m => m.Id == gone.Id);
    }

    // ── Limit ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Limit_CapsResultsAndTheReportedCount()
    {
        using var ctx = new AppDbContext(_options);
        var rules = new SmartPlaylistRules { Limit = 2, Sort = SmartPlaylistSort.Title };

        var result = await NewEvaluator(ctx).EvaluateAsync(rules, _owner, LibraryAccess.Unrestricted);
        var count = await NewEvaluator(ctx).CountAsync(rules, _owner, LibraryAccess.Unrestricted);

        Assert.Equal(2, result.Count);
        // The count has to agree with what opening the playlist shows, not with
        // how many rows matched before the cap.
        Assert.Equal(2, count);
    }

    [Fact]
    public async Task Count_ReportsMatchesWhenUnderTheLimit()
    {
        using var ctx = new AppDbContext(_options);

        var count = await NewEvaluator(ctx).CountAsync(
            new SmartPlaylistRules { AddedWithinDays = 30 }, _owner, LibraryAccess.Unrestricted);

        Assert.Equal(2, count); // _fresh and _inBlockedLibrary
    }

    [Fact]
    public async Task Ordering_IsStableAcrossReads()
    {
        using var ctx = new AppDbContext(_options);
        var rules = new SmartPlaylistRules { Sort = SmartPlaylistSort.RecentlyAdded };

        var first = await NewEvaluator(ctx).EvaluateAsync(rules, _owner, LibraryAccess.Unrestricted);
        var second = await NewEvaluator(ctx).EvaluateAsync(rules, _owner, LibraryAccess.Unrestricted);

        Assert.Equal(first.Select(m => m.Id), second.Select(m => m.Id));
        Assert.Equal(_inBlockedLibrary.Id, first[0].Id); // newest addition
    }

    [Fact]
    public async Task RecentlyPlayed_OrdersByTheOwnersLatestPlay()
    {
        using var ctx = new AppDbContext(_options);
        await PlayAsync(ctx, _owner, _old, times: 1, at: DateTime.UtcNow.AddHours(-1));
        await PlayAsync(ctx, _owner, _fresh, times: 1, at: DateTime.UtcNow.AddDays(-9));

        var result = await NewEvaluator(ctx).EvaluateAsync(
            new SmartPlaylistRules { Sort = SmartPlaylistSort.RecentlyPlayed },
            _owner, LibraryAccess.Unrestricted);

        Assert.Equal(_old.Id, result[0].Id);
    }
}
