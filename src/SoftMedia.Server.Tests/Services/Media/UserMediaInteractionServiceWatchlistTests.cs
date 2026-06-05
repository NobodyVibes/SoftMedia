using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SoftMedia.Server.Data;
using SoftMedia.Server.Models;
using SoftMedia.Server.Services.Media;
using Xunit;

namespace SoftMedia.Server.Tests.Services.Media;

/// Wave E3 — focused coverage for ToggleWatchlistAsync.
public class UserMediaInteractionServiceWatchlistTests : IDisposable
{
    private readonly AppDbContext _db;
    private readonly Guid _userId = Guid.NewGuid();
    private readonly Guid _mediaId = Guid.NewGuid();

    public UserMediaInteractionServiceWatchlistTests()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"watchlist-svc-{Guid.NewGuid()}")
            .Options;
        _db = new AppDbContext(options);

        _db.MediaItems.Add(new MediaItem
        {
            Id = _mediaId, Type = MediaType.Movie,
            Title = "Test", SortTitle = "Test", Path = "/lib/test.mkv",
            LibraryId = Guid.NewGuid(),
        });
        _db.SaveChanges();
    }

    public void Dispose() => _db.Dispose();

    private UserMediaInteractionService NewService() =>
        new(_db, NullLogger<UserMediaInteractionService>.Instance);

    [Fact]
    public async Task Add_CreatesInteractionWithStampedTimestamp()
    {
        var before = DateTime.UtcNow;
        await NewService().ToggleWatchlistAsync(_userId, _mediaId, true);

        var row = await _db.UserMediaInteractions.FirstAsync();
        Assert.True(row.IsWatchlisted);
        Assert.NotNull(row.WatchlistedAt);
        Assert.True(row.WatchlistedAt >= before);
    }

    [Fact]
    public async Task RemoveWhenNotWatchlisted_NoOp()
    {
        // No row exists — removing should not create one.
        await NewService().ToggleWatchlistAsync(_userId, _mediaId, false);

        Assert.Equal(0, await _db.UserMediaInteractions.CountAsync());
    }

    [Fact]
    public async Task ReAdd_RefreshesTimestamp()
    {
        var svc = NewService();
        await svc.ToggleWatchlistAsync(_userId, _mediaId, true);
        var first = await _db.UserMediaInteractions.AsNoTracking().FirstAsync();
        var firstStamp = first.WatchlistedAt;

        await Task.Delay(20);
        await svc.ToggleWatchlistAsync(_userId, _mediaId, false);
        await svc.ToggleWatchlistAsync(_userId, _mediaId, true);

        var second = await _db.UserMediaInteractions.AsNoTracking().FirstAsync();
        Assert.NotNull(second.WatchlistedAt);
        Assert.True(second.WatchlistedAt > firstStamp);
    }

    [Fact]
    public async Task Remove_PreservesOtherInteractionFields()
    {
        // Pre-seed with favorite + watchlisted; removing watchlist should keep
        // the favorite flag intact and the row alive.
        _db.UserMediaInteractions.Add(new UserMediaInteraction
        {
            UserId = _userId, MediaItemId = _mediaId,
            IsFavorite = true, IsWatchlisted = true, WatchlistedAt = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync();

        await NewService().ToggleWatchlistAsync(_userId, _mediaId, false);

        var row = await _db.UserMediaInteractions.FirstAsync();
        Assert.False(row.IsWatchlisted);
        Assert.Null(row.WatchlistedAt);
        Assert.True(row.IsFavorite);
    }

    [Fact]
    public async Task Remove_FullyEmptyInteraction_DeletesRow()
    {
        // If watchlist is the only thing keeping the row alive, removing it
        // should GC the row entirely.
        _db.UserMediaInteractions.Add(new UserMediaInteraction
        {
            UserId = _userId, MediaItemId = _mediaId,
            IsWatchlisted = true, WatchlistedAt = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync();

        await NewService().ToggleWatchlistAsync(_userId, _mediaId, false);

        Assert.Equal(0, await _db.UserMediaInteractions.CountAsync());
    }
}
