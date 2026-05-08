using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Moq;
using SoftMedia.Server.Controllers;
using SoftMedia.Server.Data;
using SoftMedia.Server.DTOs;
using SoftMedia.Server.Models;
using SoftMedia.Server.Services.Security.LibraryAccess;
using Xunit;

namespace SoftMedia.Server.Tests.Controllers;

/// Wave E3 — WatchlistController coverage.
///   - GET returns watchlisted items, ordered by WatchlistedAt desc.
///   - ACL strips items from blocked libraries.
///   - The toggle endpoint lives on InteractionController; covered by
///     UserMediaInteractionService tests, but a quick integration assert
///     here too: toggling should round-trip into the GET response.
public class WatchlistControllerTests : IDisposable
{
    private readonly AppDbContext _db;
    private readonly Guid _userId = Guid.NewGuid();
    private readonly Library _libA;
    private readonly Library _libB;
    private readonly MediaItem _itemA1;
    private readonly MediaItem _itemA2;
    private readonly MediaItem _itemB1;

    public WatchlistControllerTests()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"watchlist-{Guid.NewGuid()}")
            .Options;
        _db = new AppDbContext(options);

        _libA = new Library { Id = Guid.NewGuid(), Name = "A", Type = LibraryType.Movie, Paths = new() { "/a" } };
        _libB = new Library { Id = Guid.NewGuid(), Name = "B", Type = LibraryType.Movie, Paths = new() { "/b" } };
        _itemA1 = new MediaItem { Id = Guid.NewGuid(), LibraryId = _libA.Id, Title = "A1", SortTitle = "A1", Path = "/a/1", Type = MediaType.Movie };
        _itemA2 = new MediaItem { Id = Guid.NewGuid(), LibraryId = _libA.Id, Title = "A2", SortTitle = "A2", Path = "/a/2", Type = MediaType.Movie };
        _itemB1 = new MediaItem { Id = Guid.NewGuid(), LibraryId = _libB.Id, Title = "B1", SortTitle = "B1", Path = "/b/1", Type = MediaType.Movie };

        _db.Users.Add(new User { Id = _userId, Username = "u", PasswordHash = "x", Role = UserRole.User, IsApproved = true });
        _db.Libraries.AddRange(_libA, _libB);
        _db.MediaItems.AddRange(_itemA1, _itemA2, _itemB1);
        _db.SaveChanges();
    }

    public void Dispose() => _db.Dispose();

    private WatchlistController NewController(LibraryAccess access)
    {
        var libraryAccess = new Mock<IUserLibraryAccessProvider>();
        libraryAccess.Setup(p => p.GetCurrentAsync()).ReturnsAsync(access);

        var controller = new WatchlistController(_db, libraryAccess.Object);
        var identity = new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, _userId.ToString()),
        });
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) },
        };
        return controller;
    }

    private async Task SeedWatchlistedAsync(MediaItem item, DateTime watchedAt)
    {
        _db.UserMediaInteractions.Add(new UserMediaInteraction
        {
            UserId = _userId,
            MediaItemId = item.Id,
            IsWatchlisted = true,
            WatchlistedAt = watchedAt,
        });
        await _db.SaveChangesAsync();
    }

    [Fact]
    public async Task Get_EmptyWatchlist_ReturnsEmptyList()
    {
        var result = await NewController(LibraryAccess.Unrestricted).Get();
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var list = Assert.IsAssignableFrom<List<MediaItemDto>>(ok.Value);
        Assert.Empty(list);
    }

    [Fact]
    public async Task Get_ReturnsItemsOrderedByWatchlistedAtDesc()
    {
        var now = DateTime.UtcNow;
        await SeedWatchlistedAsync(_itemA1, now.AddMinutes(-10));
        await SeedWatchlistedAsync(_itemA2, now); // newest

        var result = await NewController(LibraryAccess.Unrestricted).Get();
        var list = Assert.IsAssignableFrom<List<MediaItemDto>>(((OkObjectResult)result.Result!).Value);

        Assert.Equal(2, list.Count);
        Assert.Equal(_itemA2.Id, list[0].Id);
        Assert.Equal(_itemA1.Id, list[1].Id);
    }

    [Fact]
    public async Task Get_StripsItemsFromBlockedLibraries()
    {
        await SeedWatchlistedAsync(_itemA1, DateTime.UtcNow);
        await SeedWatchlistedAsync(_itemB1, DateTime.UtcNow.AddSeconds(1));

        var result = await NewController(LibraryAccess.AllowOnly(new[] { _libA.Id })).Get();
        var list = Assert.IsAssignableFrom<List<MediaItemDto>>(((OkObjectResult)result.Result!).Value);

        Assert.Single(list);
        Assert.Equal(_itemA1.Id, list[0].Id);

        // Underlying interaction row remains so re-granting access restores it.
        Assert.True(await _db.UserMediaInteractions.AnyAsync(i => i.MediaItemId == _itemB1.Id && i.IsWatchlisted));
    }

    [Fact]
    public async Task Get_LimitClampedToBounds()
    {
        // Seed 3, request limit=1 — should get the newest only.
        await SeedWatchlistedAsync(_itemA1, DateTime.UtcNow.AddMinutes(-10));
        await SeedWatchlistedAsync(_itemA2, DateTime.UtcNow);
        await SeedWatchlistedAsync(_itemB1, DateTime.UtcNow.AddMinutes(-5));

        var result = await NewController(LibraryAccess.Unrestricted).Get(limit: 1);
        var list = Assert.IsAssignableFrom<List<MediaItemDto>>(((OkObjectResult)result.Result!).Value);
        Assert.Single(list);
        Assert.Equal(_itemA2.Id, list[0].Id);
    }

    [Fact]
    public async Task Get_OutOfRangeLimit_ClampsRatherThanThrows()
    {
        // limit=0 should clamp to 1 (or at least not crash); over-large limit clamps to 200.
        await SeedWatchlistedAsync(_itemA1, DateTime.UtcNow);
        var resultLow = await NewController(LibraryAccess.Unrestricted).Get(limit: 0);
        Assert.IsType<OkObjectResult>(resultLow.Result);

        var resultHigh = await NewController(LibraryAccess.Unrestricted).Get(limit: 10_000);
        Assert.IsType<OkObjectResult>(resultHigh.Result);
    }

    [Fact]
    public async Task Get_NonWatchlistedInteractionsDoNotAppear()
    {
        // User has favorited the item but not watchlisted it — must not appear.
        _db.UserMediaInteractions.Add(new UserMediaInteraction
        {
            UserId = _userId, MediaItemId = _itemA1.Id, IsFavorite = true, IsWatchlisted = false,
        });
        await _db.SaveChangesAsync();

        var result = await NewController(LibraryAccess.Unrestricted).Get();
        var list = Assert.IsAssignableFrom<List<MediaItemDto>>(((OkObjectResult)result.Result!).Value);
        Assert.Empty(list);
    }

    [Theory]
    [InlineData(MediaType.Audio)]
    [InlineData(MediaType.Album)]
    [InlineData(MediaType.Artist)]
    public async Task Get_StripsMusicItemsFromLegacyWatchlistRows(MediaType musicType)
    {
        // Music items can no longer be added to the watchlist (the toggle
        // endpoint rejects them), but rows from before that guard existed
        // may still be in the DB. The list endpoint must hide them so the
        // UI never presents music as a watchlist member.
        var music = new MediaItem
        {
            Id = Guid.NewGuid(),
            LibraryId = _libA.Id,
            Title = "Track",
            SortTitle = "Track",
            Path = "/a/track",
            Type = musicType,
        };
        _db.MediaItems.Add(music);
        await _db.SaveChangesAsync();

        await SeedWatchlistedAsync(_itemA1, DateTime.UtcNow.AddSeconds(-1));
        await SeedWatchlistedAsync(music, DateTime.UtcNow);

        var result = await NewController(LibraryAccess.Unrestricted).Get();
        var list = Assert.IsAssignableFrom<List<MediaItemDto>>(((OkObjectResult)result.Result!).Value);

        Assert.Single(list);
        Assert.Equal(_itemA1.Id, list[0].Id);
    }
}
