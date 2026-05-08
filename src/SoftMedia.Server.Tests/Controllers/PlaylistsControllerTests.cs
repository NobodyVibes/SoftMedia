using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SoftMedia.Server.Controllers;
using SoftMedia.Server.Data;
using SoftMedia.Server.DTOs;
using SoftMedia.Server.Models;
using SoftMedia.Server.Services.Security.LibraryAccess;
using Xunit;

namespace SoftMedia.Server.Tests.Controllers;

/// Wave E1 — PlaylistsController behavioural coverage.
///
/// Tests focus on the pieces the engineering plan calls out:
///   - private-by-default visibility,
///   - non-owner can read public, can't read private,
///   - audio-only validation,
///   - reorder permutation check (by PlaylistItem.Id, not MediaItemId),
///   - duplicates allowed within a playlist,
///   - ACL strips items from a public playlist on read,
///   - mutation endpoints are owner-only and admins do NOT bypass.
public class PlaylistsControllerTests : IDisposable
{
    private readonly AppDbContext _db;
    private readonly Guid _ownerId = Guid.NewGuid();
    private readonly Guid _otherId = Guid.NewGuid();
    private readonly Guid _adminId = Guid.NewGuid();
    private readonly Library _libA;
    private readonly Library _libB;
    private readonly MediaItem _audioA1;
    private readonly MediaItem _audioA2;
    private readonly MediaItem _audioB1;
    private readonly MediaItem _movieA;

    public PlaylistsControllerTests()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"playlists-{Guid.NewGuid()}")
            .Options;
        _db = new AppDbContext(options);

        _libA = new Library { Id = Guid.NewGuid(), Name = "Lib A", Type = LibraryType.Music, Paths = new() { "/a" } };
        _libB = new Library { Id = Guid.NewGuid(), Name = "Lib B", Type = LibraryType.Music, Paths = new() { "/b" } };
        _audioA1 = new MediaItem { Id = Guid.NewGuid(), LibraryId = _libA.Id, Title = "A1", SortTitle = "A1", Path = "/a/1", Type = MediaType.Audio };
        _audioA2 = new MediaItem { Id = Guid.NewGuid(), LibraryId = _libA.Id, Title = "A2", SortTitle = "A2", Path = "/a/2", Type = MediaType.Audio };
        _audioB1 = new MediaItem { Id = Guid.NewGuid(), LibraryId = _libB.Id, Title = "B1", SortTitle = "B1", Path = "/b/1", Type = MediaType.Audio };
        _movieA = new MediaItem { Id = Guid.NewGuid(), LibraryId = _libA.Id, Title = "Movie", SortTitle = "Movie", Path = "/a/m.mkv", Type = MediaType.Movie };

        _db.Libraries.AddRange(_libA, _libB);
        _db.MediaItems.AddRange(_audioA1, _audioA2, _audioB1, _movieA);
        _db.Users.AddRange(
            new User { Id = _ownerId, Username = "owner", PasswordHash = "x", Role = UserRole.User, IsApproved = true },
            new User { Id = _otherId, Username = "other", PasswordHash = "x", Role = UserRole.User, IsApproved = true },
            new User { Id = _adminId, Username = "admin", PasswordHash = "x", Role = UserRole.Admin, IsApproved = true });
        _db.SaveChanges();
    }

    public void Dispose() => _db.Dispose();

    private PlaylistsController NewController(Guid actingUserId, LibraryAccess access)
    {
        var libraryAccess = new Mock<IUserLibraryAccessProvider>();
        libraryAccess.Setup(p => p.GetCurrentAsync()).ReturnsAsync(access);

        var controller = new PlaylistsController(
            _db, libraryAccess.Object, NullLogger<PlaylistsController>.Instance);

        var identity = new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, actingUserId.ToString()),
        });
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) },
        };
        return controller;
    }

    private async Task<Playlist> SeedPlaylistAsync(Guid ownerId, bool isPublic, params Guid[] tracks)
    {
        var playlist = new Playlist { OwnerUserId = ownerId, Name = "Mix", IsPublic = isPublic };
        _db.Playlists.Add(playlist);
        await _db.SaveChangesAsync();
        var order = 0;
        foreach (var trackId in tracks)
        {
            _db.PlaylistItems.Add(new PlaylistItem
            {
                PlaylistId = playlist.Id, MediaItemId = trackId, Order = order++,
            });
        }
        await _db.SaveChangesAsync();
        return playlist;
    }

    // ── Create ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Create_DefaultsToPrivate()
    {
        var controller = NewController(_ownerId, LibraryAccess.Unrestricted);

        var result = await controller.Create(new CreatePlaylistRequest { Name = "My Mix" });

        var created = Assert.IsType<CreatedAtActionResult>(result.Result);
        var dto = Assert.IsType<PlaylistSummaryDto>(created.Value);
        Assert.False(dto.IsPublic);
        Assert.True(dto.IsOwner);
        Assert.Equal("My Mix", dto.Name);
    }

    [Fact]
    public async Task Create_RejectsEmptyName()
    {
        var controller = NewController(_ownerId, LibraryAccess.Unrestricted);
        var result = await controller.Create(new CreatePlaylistRequest { Name = "" });
        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task Create_RejectsOverlongName()
    {
        var controller = NewController(_ownerId, LibraryAccess.Unrestricted);
        var result = await controller.Create(new CreatePlaylistRequest { Name = new string('a', 121) });
        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    // ── List / Get visibility ────────────────────────────────────────────────

    [Fact]
    public async Task List_OwnerSeesOwnPrivate()
    {
        await SeedPlaylistAsync(_ownerId, isPublic: false);
        var result = await NewController(_ownerId, LibraryAccess.Unrestricted).List();

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var list = Assert.IsAssignableFrom<List<PlaylistSummaryDto>>(ok.Value);
        Assert.Single(list);
        Assert.True(list[0].IsOwner);
    }

    [Fact]
    public async Task List_OtherUserDoesNotSeePrivate()
    {
        await SeedPlaylistAsync(_ownerId, isPublic: false);
        var result = await NewController(_otherId, LibraryAccess.Unrestricted).List();

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var list = Assert.IsAssignableFrom<List<PlaylistSummaryDto>>(ok.Value);
        Assert.Empty(list);
    }

    [Fact]
    public async Task List_OtherUserSeesPublicAsNonOwner()
    {
        await SeedPlaylistAsync(_ownerId, isPublic: true);
        var result = await NewController(_otherId, LibraryAccess.Unrestricted).List();

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var list = Assert.IsAssignableFrom<List<PlaylistSummaryDto>>(ok.Value);
        Assert.Single(list);
        Assert.False(list[0].IsOwner);
    }

    [Fact]
    public async Task Get_PrivatePlaylist_NonOwnerReturns404()
    {
        var p = await SeedPlaylistAsync(_ownerId, isPublic: false);
        var result = await NewController(_otherId, LibraryAccess.Unrestricted).Get(p.Id);
        Assert.IsType<NotFoundResult>(result.Result);
    }

    [Fact]
    public async Task Get_PublicPlaylist_NonOwnerCanReadButIsOwnerFalse()
    {
        var p = await SeedPlaylistAsync(_ownerId, isPublic: true, _audioA1.Id);
        var result = await NewController(_otherId, LibraryAccess.Unrestricted).Get(p.Id);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsType<PlaylistDetailDto>(ok.Value);
        Assert.False(dto.IsOwner);
        Assert.Single(dto.Items);
    }

    // ── ACL stripping ────────────────────────────────────────────────────────

    [Fact]
    public async Task Get_PublicPlaylist_AclStripsBlockedTracks()
    {
        // Owner has audio in libA AND libB; viewer can only see libA.
        var p = await SeedPlaylistAsync(_ownerId, isPublic: true, _audioA1.Id, _audioB1.Id, _audioA2.Id);
        var viewerAccess = LibraryAccess.AllowOnly(new[] { _libA.Id });

        var result = await NewController(_otherId, viewerAccess).Get(p.Id);

        var dto = Assert.IsType<PlaylistDetailDto>(((OkObjectResult)result.Result!).Value);
        Assert.Equal(2, dto.Items.Count); // _audioB1 stripped
        Assert.All(dto.Items, e => Assert.NotEqual(_libB.Id, e.Media.LibraryId));
    }

    // ── Audio-only validation ────────────────────────────────────────────────

    [Fact]
    public async Task AddItems_RejectsNonAudio()
    {
        var p = await SeedPlaylistAsync(_ownerId, isPublic: false);
        var controller = NewController(_ownerId, LibraryAccess.Unrestricted);

        var result = await controller.AddItems(p.Id, new AddPlaylistItemsRequest
        {
            MediaItemIds = new List<Guid> { _audioA1.Id, _movieA.Id },
        });

        Assert.IsType<BadRequestObjectResult>(result);
        // No items inserted on a partially-invalid request.
        var rows = await _db.PlaylistItems.Where(pi => pi.PlaylistId == p.Id).CountAsync();
        Assert.Equal(0, rows);
    }

    [Fact]
    public async Task AddItems_AppendsInRequestOrder()
    {
        var p = await SeedPlaylistAsync(_ownerId, isPublic: false, _audioA2.Id); // pre-seeded at order 0
        var controller = NewController(_ownerId, LibraryAccess.Unrestricted);

        await controller.AddItems(p.Id, new AddPlaylistItemsRequest
        {
            MediaItemIds = new List<Guid> { _audioA1.Id, _audioB1.Id },
        });

        var entries = await _db.PlaylistItems
            .Where(pi => pi.PlaylistId == p.Id)
            .OrderBy(pi => pi.Order)
            .ToListAsync();

        Assert.Equal(3, entries.Count);
        Assert.Equal(_audioA2.Id, entries[0].MediaItemId);
        Assert.Equal(_audioA1.Id, entries[1].MediaItemId);
        Assert.Equal(_audioB1.Id, entries[2].MediaItemId);
    }

    [Fact]
    public async Task AddItems_DuplicateMediaItem_StoredTwice()
    {
        // Research finding: users intentionally duplicate tracks (Spotify, etc.).
        // Schema is surrogate-PK so this works; assert it.
        var p = await SeedPlaylistAsync(_ownerId, isPublic: false);
        var controller = NewController(_ownerId, LibraryAccess.Unrestricted);

        await controller.AddItems(p.Id, new AddPlaylistItemsRequest
        {
            MediaItemIds = new List<Guid> { _audioA1.Id, _audioA1.Id },
        });

        var entries = await _db.PlaylistItems.Where(pi => pi.PlaylistId == p.Id).ToListAsync();
        Assert.Equal(2, entries.Count);
        Assert.All(entries, e => Assert.Equal(_audioA1.Id, e.MediaItemId));
    }

    // ── Reorder ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Reorder_PermutationByPlaylistItemId_Succeeds()
    {
        var p = await SeedPlaylistAsync(_ownerId, isPublic: false, _audioA1.Id, _audioA2.Id, _audioB1.Id);
        var entries = await _db.PlaylistItems
            .Where(pi => pi.PlaylistId == p.Id)
            .OrderBy(pi => pi.Order)
            .ToListAsync();

        // Reverse the order.
        var newOrder = entries.AsEnumerable().Reverse().Select(e => e.Id).ToList();

        var controller = NewController(_ownerId, LibraryAccess.Unrestricted);
        var result = await controller.Reorder(p.Id, new ReorderPlaylistRequest { ItemIds = newOrder });

        Assert.IsType<NoContentResult>(result);

        var afterOrder = await _db.PlaylistItems
            .Where(pi => pi.PlaylistId == p.Id)
            .OrderBy(pi => pi.Order)
            .Select(pi => pi.MediaItemId)
            .ToListAsync();

        Assert.Equal(_audioB1.Id, afterOrder[0]);
        Assert.Equal(_audioA2.Id, afterOrder[1]);
        Assert.Equal(_audioA1.Id, afterOrder[2]);
    }

    [Fact]
    public async Task Reorder_RejectsAdditions()
    {
        var p = await SeedPlaylistAsync(_ownerId, isPublic: false, _audioA1.Id, _audioA2.Id);
        var entries = await _db.PlaylistItems.Where(pi => pi.PlaylistId == p.Id).ToListAsync();
        var ids = entries.Select(e => e.Id).ToList();
        ids.Add(Guid.NewGuid()); // bogus addition

        var result = await NewController(_ownerId, LibraryAccess.Unrestricted)
            .Reorder(p.Id, new ReorderPlaylistRequest { ItemIds = ids });

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task Reorder_RejectsRemovals()
    {
        var p = await SeedPlaylistAsync(_ownerId, isPublic: false, _audioA1.Id, _audioA2.Id);
        var entries = await _db.PlaylistItems.Where(pi => pi.PlaylistId == p.Id).ToListAsync();
        var ids = entries.Take(1).Select(e => e.Id).ToList(); // missing one

        var result = await NewController(_ownerId, LibraryAccess.Unrestricted)
            .Reorder(p.Id, new ReorderPlaylistRequest { ItemIds = ids });

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task Reorder_RejectsDuplicates()
    {
        var p = await SeedPlaylistAsync(_ownerId, isPublic: false, _audioA1.Id, _audioA2.Id);
        var entries = await _db.PlaylistItems.Where(pi => pi.PlaylistId == p.Id).ToListAsync();
        var firstId = entries[0].Id;
        // Submit the same id twice.
        var result = await NewController(_ownerId, LibraryAccess.Unrestricted)
            .Reorder(p.Id, new ReorderPlaylistRequest { ItemIds = new List<Guid> { firstId, firstId } });

        Assert.IsType<BadRequestObjectResult>(result);
    }

    // ── Owner-only mutation ──────────────────────────────────────────────────

    [Fact]
    public async Task Update_NonOwnerReturns404()
    {
        var p = await SeedPlaylistAsync(_ownerId, isPublic: true);
        var result = await NewController(_otherId, LibraryAccess.Unrestricted)
            .Update(p.Id, new UpdatePlaylistRequest { Name = "Hijacked" });

        Assert.IsType<NotFoundResult>(result);

        var stored = await _db.Playlists.FirstAsync(x => x.Id == p.Id);
        Assert.NotEqual("Hijacked", stored.Name);
    }

    [Fact]
    public async Task Update_AdminDoesNotBypass()
    {
        // Plan rule: admins do NOT bypass — playlists are user data.
        var p = await SeedPlaylistAsync(_ownerId, isPublic: true);
        var result = await NewController(_adminId, LibraryAccess.Unrestricted)
            .Update(p.Id, new UpdatePlaylistRequest { Name = "AdminEdit" });

        Assert.IsType<NotFoundResult>(result);

        var stored = await _db.Playlists.FirstAsync(x => x.Id == p.Id);
        Assert.NotEqual("AdminEdit", stored.Name);
    }

    [Fact]
    public async Task Delete_NonOwnerReturns404()
    {
        var p = await SeedPlaylistAsync(_ownerId, isPublic: true);
        var result = await NewController(_otherId, LibraryAccess.Unrestricted).Delete(p.Id);
        Assert.IsType<NotFoundResult>(result);
        Assert.True(await _db.Playlists.AnyAsync(x => x.Id == p.Id));
    }

    [Fact]
    public async Task Update_FlipsPublicWithOwner()
    {
        var p = await SeedPlaylistAsync(_ownerId, isPublic: false);
        var controller = NewController(_ownerId, LibraryAccess.Unrestricted);

        await controller.Update(p.Id, new UpdatePlaylistRequest { IsPublic = true });

        var updated = await _db.Playlists.FirstAsync(x => x.Id == p.Id);
        Assert.True(updated.IsPublic);
    }

    [Fact]
    public async Task RemoveItem_CompactsOrderValues()
    {
        // Ensure removing the middle item leaves the remaining items at orders 0,1.
        var p = await SeedPlaylistAsync(_ownerId, isPublic: false, _audioA1.Id, _audioA2.Id, _audioB1.Id);
        var middle = await _db.PlaylistItems.FirstAsync(pi => pi.PlaylistId == p.Id && pi.Order == 1);

        var controller = NewController(_ownerId, LibraryAccess.Unrestricted);
        await controller.RemoveItem(p.Id, middle.Id);

        var remaining = await _db.PlaylistItems
            .Where(pi => pi.PlaylistId == p.Id)
            .OrderBy(pi => pi.Order)
            .ToListAsync();
        Assert.Equal(2, remaining.Count);
        Assert.Equal(0, remaining[0].Order);
        Assert.Equal(1, remaining[1].Order);
    }
}
