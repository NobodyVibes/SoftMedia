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
using SoftMedia.Server.Services.Media;
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
    private readonly MediaItem _audioNoArt;
    private readonly MediaItem _movieA;
    private readonly MediaItem _albumOne;
    private readonly MediaItem _albumTwo;

    public PlaylistsControllerTests()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"playlists-{Guid.NewGuid()}")
            .Options;
        _db = new AppDbContext(options);

        _libA = new Library { Id = Guid.NewGuid(), Name = "Lib A", Type = LibraryType.Music, Paths = new() { "/a" } };
        _libB = new Library { Id = Guid.NewGuid(), Name = "Lib B", Type = LibraryType.Music, Paths = new() { "/b" } };
        // Cover art resolves through the album, so the audio rows need albums:
        // A1+A2 share one (mosaic de-duplication), B1 has its own in the other
        // library (ACL filtering), and _audioNoArt has none at all.
        _albumOne = new MediaItem { Id = Guid.NewGuid(), LibraryId = _libA.Id, Title = "Album One", SortTitle = "Album One", Path = "/a", Type = MediaType.Album, CoverArtPath = "/cache/a.jpg" };
        _albumTwo = new MediaItem { Id = Guid.NewGuid(), LibraryId = _libB.Id, Title = "Album Two", SortTitle = "Album Two", Path = "/b", Type = MediaType.Album, CoverArtPath = "/cache/b.jpg" };
        _audioA1 = new MediaItem { Id = Guid.NewGuid(), LibraryId = _libA.Id, Title = "A1", SortTitle = "A1", Path = "/a/1", Type = MediaType.Audio, AlbumId = _albumOne.Id };
        _audioA2 = new MediaItem { Id = Guid.NewGuid(), LibraryId = _libA.Id, Title = "A2", SortTitle = "A2", Path = "/a/2", Type = MediaType.Audio, AlbumId = _albumOne.Id };
        _audioB1 = new MediaItem { Id = Guid.NewGuid(), LibraryId = _libB.Id, Title = "B1", SortTitle = "B1", Path = "/b/1", Type = MediaType.Audio, AlbumId = _albumTwo.Id };
        _audioNoArt = new MediaItem { Id = Guid.NewGuid(), LibraryId = _libA.Id, Title = "Loose", SortTitle = "Loose", Path = "/a/loose.mp3", Type = MediaType.Audio };
        _movieA = new MediaItem { Id = Guid.NewGuid(), LibraryId = _libA.Id, Title = "Movie", SortTitle = "Movie", Path = "/a/m.mkv", Type = MediaType.Movie };

        _db.Libraries.AddRange(_libA, _libB);
        _db.MediaItems.AddRange(_albumOne, _albumTwo, _audioA1, _audioA2, _audioB1, _audioNoArt, _movieA);
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

        // A real evaluator over the same context. Its SQL translation is covered
        // separately against SQLite (SmartPlaylistEvaluatorTests) — here it just
        // needs to produce the right membership for the controller's behaviour.
        var controller = new PlaylistsController(
            _db, libraryAccess.Object, new SmartPlaylistEvaluator(_db),
            NullLogger<PlaylistsController>.Instance);

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

    // ── Cover mosaic ─────────────────────────────────────────────────────────

    [Fact]
    public async Task List_CoverArt_DeDupesByAlbumAndPreservesPlayOrder()
    {
        // _audioA1 and _audioA2 share _albumOne, so the mosaic must show that
        // sleeve once — not twice — with _albumTwo's cover second.
        await SeedPlaylistAsync(_ownerId, isPublic: false, _audioA1.Id, _audioA2.Id, _audioB1.Id);

        var result = await NewController(_ownerId, LibraryAccess.Unrestricted).List();
        var list = Assert.IsAssignableFrom<List<PlaylistSummaryDto>>(((OkObjectResult)result.Result!).Value);

        Assert.Equal(
            new[]
            {
                $"/api/v1/music/album/{_albumOne.Id}/cover",
                $"/api/v1/music/album/{_albumTwo.Id}/cover",
            },
            list[0].CoverImagePaths);
    }

    [Fact]
    public async Task List_CoverArt_ExcludesTracksBlockedByLibraryAcl()
    {
        // A public playlist spanning both libraries, seen by a viewer allowed only
        // libA: _audioB1's sleeve is content the viewer can't see, so it must not
        // reach the mosaic even though the playlist itself is visible.
        await SeedPlaylistAsync(_ownerId, isPublic: true, _audioB1.Id, _audioA1.Id);

        var result = await NewController(_otherId, LibraryAccess.AllowOnly(new[] { _libA.Id })).List();
        var list = Assert.IsAssignableFrom<List<PlaylistSummaryDto>>(((OkObjectResult)result.Result!).Value);

        Assert.Equal(new[] { $"/api/v1/music/album/{_albumOne.Id}/cover" }, list[0].CoverImagePaths);
    }

    [Fact]
    public async Task List_CoverArt_EmptyWhenNoTrackHasArtwork()
    {
        // A track with no album and no poster resolves to no path at all; the
        // client falls back to its gradient tile rather than a broken image.
        await SeedPlaylistAsync(_ownerId, isPublic: false, _audioNoArt.Id);

        var result = await NewController(_ownerId, LibraryAccess.Unrestricted).List();
        var list = Assert.IsAssignableFrom<List<PlaylistSummaryDto>>(((OkObjectResult)result.Result!).Value);

        Assert.Empty(list[0].CoverImagePaths);
    }

    [Fact]
    public async Task List_CoverArt_EmptyForEmptyPlaylist()
    {
        await SeedPlaylistAsync(_ownerId, isPublic: false);

        var result = await NewController(_ownerId, LibraryAccess.Unrestricted).List();
        var list = Assert.IsAssignableFrom<List<PlaylistSummaryDto>>(((OkObjectResult)result.Result!).Value);

        Assert.Empty(list[0].CoverImagePaths);
    }

    // ── ACL stripping ────────────────────────────────────────────────────────

    [Fact]
    public async Task List_ItemCount_CountsOnlyTracksTheViewerCanSee()
    {
        // The card's count and the detail page's list must describe the same
        // population. Counting every row put "3 tracks" on a card that opened
        // showing two, because _audioB1 lives in a library this viewer is denied.
        var p = await SeedPlaylistAsync(_ownerId, isPublic: true, _audioA1.Id, _audioB1.Id, _audioA2.Id);
        var viewerAccess = LibraryAccess.AllowOnly(new[] { _libA.Id });

        var listResult = await NewController(_otherId, viewerAccess).List();
        var list = Assert.IsAssignableFrom<List<PlaylistSummaryDto>>(((OkObjectResult)listResult.Result!).Value);

        var detailResult = await NewController(_otherId, viewerAccess).Get(p.Id);
        var detail = Assert.IsType<PlaylistDetailDto>(((OkObjectResult)detailResult.Result!).Value);

        Assert.Equal(2, list[0].ItemCount);
        Assert.Equal(detail.Items.Count, list[0].ItemCount);
    }

    [Fact]
    public async Task List_OrdersMostRecentlyUpdatedFirst()
    {
        // The client surfaces this as "Updated 3 days ago" on each card and relies
        // on the server's order rather than sorting again.
        var older = await SeedPlaylistAsync(_ownerId, isPublic: false);
        var newer = await SeedPlaylistAsync(_ownerId, isPublic: false);
        older.UpdatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        newer.UpdatedAt = new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc);
        await _db.SaveChangesAsync();

        var result = await NewController(_ownerId, LibraryAccess.Unrestricted).List();
        var list = Assert.IsAssignableFrom<List<PlaylistSummaryDto>>(((OkObjectResult)result.Result!).Value);

        Assert.Equal(new[] { newer.Id, older.Id }, list.Select(p => p.Id));
    }

    [Fact]
    public async Task List_ItemCount_UnrestrictedViewerSeesEveryTrack()
    {
        await SeedPlaylistAsync(_ownerId, isPublic: false, _audioA1.Id, _audioB1.Id, _audioA2.Id);

        var result = await NewController(_ownerId, LibraryAccess.Unrestricted).List();
        var list = Assert.IsAssignableFrom<List<PlaylistSummaryDto>>(((OkObjectResult)result.Result!).Value);

        Assert.Equal(3, list[0].ItemCount);
    }

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

    // ── M3U interchange ──────────────────────────────────────────────────────

    private static string BodyOf(IActionResult result)
        => System.Text.Encoding.UTF8.GetString(Assert.IsType<FileContentResult>(result).FileContents);

    [Fact]
    public async Task Export_WritesTheTracksInPlaylistOrder()
    {
        var p = await SeedPlaylistAsync(_ownerId, isPublic: false, _audioA2.Id, _audioA1.Id);

        var content = BodyOf(await NewController(_ownerId, LibraryAccess.Unrestricted).Export(p.Id));

        var paths = SoftMedia.Server.Services.Media.M3uPlaylistFormat.ParsePaths(content);
        Assert.Equal(new[] { _audioA2.Path, _audioA1.Path }, paths);
        Assert.Contains("#PLAYLIST:Mix", content);
    }

    [Fact]
    public async Task Export_OmitsTracksTheViewerCannotSee()
    {
        var p = await SeedPlaylistAsync(_ownerId, isPublic: true, _audioA1.Id, _audioB1.Id);

        var content = BodyOf(await NewController(_otherId, LibraryAccess.AllowOnly(new[] { _libA.Id })).Export(p.Id));

        Assert.Contains(_audioA1.Path, content);
        Assert.DoesNotContain(_audioB1.Path, content);
    }

    [Fact]
    public async Task Export_OfASmartPlaylistWritesItsCurrentSnapshot()
    {
        var p = await SeedSmartPlaylistAsync(_ownerId, new SmartPlaylistRules());

        var content = BodyOf(await NewController(_ownerId, LibraryAccess.Unrestricted).Export(p.Id));

        Assert.Equal(4, SoftMedia.Server.Services.Media.M3uPlaylistFormat.ParsePaths(content).Count);
    }

    [Fact]
    public async Task Export_PrivatePlaylistIsNotReadableByOthers()
    {
        var p = await SeedPlaylistAsync(_ownerId, isPublic: false, _audioA1.Id);

        var result = await NewController(_otherId, LibraryAccess.Unrestricted).Export(p.Id);

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task Import_MatchesTracksByExactPath()
    {
        var content = $"#EXTM3U\n#PLAYLIST:From File\n{_audioA1.Path}\n{_audioA2.Path}\n";

        var result = await NewController(_ownerId, LibraryAccess.Unrestricted)
            .Import(new ImportPlaylistRequest { Content = content });

        var dto = Assert.IsType<ImportPlaylistResultDto>(((OkObjectResult)result.Result!).Value);
        Assert.Equal(2, dto.MatchedCount);
        Assert.Equal(0, dto.UnmatchedCount);
        Assert.Equal("From File", dto.Playlist.Name);

        var stored = await _db.PlaylistItems
            .Where(pi => pi.PlaylistId == dto.Playlist.Id).OrderBy(pi => pi.Order)
            .Select(pi => pi.MediaItemId).ToListAsync();
        Assert.Equal(new[] { _audioA1.Id, _audioA2.Id }, stored);
    }

    // The point of importing at all: the file usually comes from another machine,
    // where the library is mounted somewhere else but the filenames still match.
    [Fact]
    public async Task Import_FallsBackToFileNamesWhenThePrefixDiffers()
    {
        // _audioNoArt is "/a/loose.mp3" — a filename unique across the fixture, so
        // the fallback has exactly one candidate to land on.
        var content = $"#EXTM3U\n/somewhere/else/{System.IO.Path.GetFileName(_audioNoArt.Path)}\n";

        var result = await NewController(_ownerId, LibraryAccess.Unrestricted)
            .Import(new ImportPlaylistRequest { Content = content });

        var dto = Assert.IsType<ImportPlaylistResultDto>(((OkObjectResult)result.Result!).Value);
        Assert.Equal(1, dto.MatchedCount);
        var stored = await _db.PlaylistItems
            .Where(pi => pi.PlaylistId == dto.Playlist.Id).Select(pi => pi.MediaItemId).ToListAsync();
        Assert.Equal(_audioNoArt.Id, stored.Single());
    }

    [Fact]
    public async Task Import_ReportsWhatItCouldNotMatch()
    {
        var content = $"#EXTM3U\n{_audioA1.Path}\n/nowhere/ghost.flac\n";

        var result = await NewController(_ownerId, LibraryAccess.Unrestricted)
            .Import(new ImportPlaylistRequest { Content = content });

        var dto = Assert.IsType<ImportPlaylistResultDto>(((OkObjectResult)result.Result!).Value);
        Assert.Equal(1, dto.MatchedCount);
        Assert.Equal(1, dto.UnmatchedCount);
        Assert.Contains("/nowhere/ghost.flac", dto.UnmatchedSample);
    }

    [Fact]
    public async Task Import_RejectsAFileThatMatchesNothing()
    {
        var result = await NewController(_ownerId, LibraryAccess.Unrestricted)
            .Import(new ImportPlaylistRequest { Content = "#EXTM3U\n/nowhere/a.flac\n" });

        Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.False(await _db.Playlists.AnyAsync(p => p.Name == "Imported Playlist"));
    }

    [Fact]
    public async Task Import_RejectsEmptyAndDirectiveOnlyFiles()
    {
        var controller = NewController(_ownerId, LibraryAccess.Unrestricted);

        Assert.IsType<BadRequestObjectResult>(
            (await controller.Import(new ImportPlaylistRequest { Content = "" })).Result);
        Assert.IsType<BadRequestObjectResult>(
            (await controller.Import(new ImportPlaylistRequest { Content = "#EXTM3U\n" })).Result);
    }

    [Fact]
    public async Task Import_WillNotPullInTracksFromADeniedLibrary()
    {
        // A filename that exists ONLY in the denied library, so a match could not
        // come from anywhere else.
        var hidden = new MediaItem
        {
            Id = Guid.NewGuid(), LibraryId = _libB.Id, Title = "Hidden", SortTitle = "Hidden",
            Path = "/b/private-recording.flac", Type = MediaType.Audio,
        };
        _db.MediaItems.Add(hidden);
        await _db.SaveChangesAsync();

        var result = await NewController(_ownerId, LibraryAccess.AllowOnly(new[] { _libA.Id }))
            .Import(new ImportPlaylistRequest { Content = $"#EXTM3U\n{hidden.Path}\n" });

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    // "01.mp3" lives in every album folder. Guessing which one the playlist meant
    // would import the wrong track under a name that looks right.
    [Fact]
    public async Task Import_RefusesToGuessBetweenIdenticallyNamedFiles()
    {
        _db.MediaItems.AddRange(
            new MediaItem
            {
                Id = Guid.NewGuid(), LibraryId = _libA.Id, Title = "One", SortTitle = "One",
                Path = "/a/album-one/01.mp3", Type = MediaType.Audio,
            },
            new MediaItem
            {
                Id = Guid.NewGuid(), LibraryId = _libA.Id, Title = "Two", SortTitle = "Two",
                Path = "/a/album-two/01.mp3", Type = MediaType.Audio,
            });
        await _db.SaveChangesAsync();

        var result = await NewController(_ownerId, LibraryAccess.Unrestricted)
            .Import(new ImportPlaylistRequest { Content = "#EXTM3U\n/elsewhere/01.mp3\n" });

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    // ...but the parent folder disambiguates, which is what makes the fallback
    // usable on a library mounted at a different root.
    [Fact]
    public async Task Import_UsesTheParentFolderToPickBetweenSameNamedFiles()
    {
        var wanted = new MediaItem
        {
            Id = Guid.NewGuid(), LibraryId = _libA.Id, Title = "One", SortTitle = "One",
            Path = "/a/album-one/01.mp3", Type = MediaType.Audio,
        };
        _db.MediaItems.AddRange(wanted, new MediaItem
        {
            Id = Guid.NewGuid(), LibraryId = _libA.Id, Title = "Two", SortTitle = "Two",
            Path = "/a/album-two/01.mp3", Type = MediaType.Audio,
        });
        await _db.SaveChangesAsync();

        var result = await NewController(_ownerId, LibraryAccess.Unrestricted)
            .Import(new ImportPlaylistRequest { Content = "#EXTM3U\nD:\\Music\\album-one\\01.mp3\n" });

        var dto = Assert.IsType<ImportPlaylistResultDto>(((OkObjectResult)result.Result!).Value);
        Assert.Equal(1, dto.MatchedCount);
        var stored = await _db.PlaylistItems
            .Where(pi => pi.PlaylistId == dto.Playlist.Id).Select(pi => pi.MediaItemId).ToListAsync();
        Assert.Equal(wanted.Id, stored.Single());
    }

    [Fact]
    public async Task Import_IgnoresNonAudioEvenOnAnExactPathMatch()
    {
        var content = $"#EXTM3U\n{_movieA.Path}\n";

        var result = await NewController(_ownerId, LibraryAccess.Unrestricted)
            .Import(new ImportPlaylistRequest { Content = content });

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task Import_PrefersAnExplicitNameOverTheFilesOwn()
    {
        var content = $"#EXTM3U\n#PLAYLIST:From File\n{_audioA1.Path}\n";

        var result = await NewController(_ownerId, LibraryAccess.Unrestricted)
            .Import(new ImportPlaylistRequest { Content = content, Name = "My Name" });

        var dto = Assert.IsType<ImportPlaylistResultDto>(((OkObjectResult)result.Result!).Value);
        Assert.Equal("My Name", dto.Playlist.Name);
    }

    [Fact]
    public async Task Import_CreatesAManualPlaylist()
    {
        var result = await NewController(_ownerId, LibraryAccess.Unrestricted)
            .Import(new ImportPlaylistRequest { Content = $"#EXTM3U\n{_audioA1.Path}\n" });

        var dto = Assert.IsType<ImportPlaylistResultDto>(((OkObjectResult)result.Result!).Value);
        Assert.Equal(PlaylistKind.Manual, dto.Playlist.Kind);
        Assert.False(dto.Playlist.IsPublic);
    }

    // ── Search ───────────────────────────────────────────────────────────────

    private async Task<Playlist> SeedNamedPlaylistAsync(
        Guid ownerId, string name, bool isPublic = false, string? description = null)
    {
        var playlist = new Playlist
        {
            OwnerUserId = ownerId, Name = name, IsPublic = isPublic, Description = description,
        };
        _db.Playlists.Add(playlist);
        await _db.SaveChangesAsync();
        return playlist;
    }

    [Fact]
    public async Task Search_MatchesOnName()
    {
        await SeedNamedPlaylistAsync(_ownerId, "Road Trip");
        await SeedNamedPlaylistAsync(_ownerId, "Dinner Party");

        var result = await NewController(_ownerId, LibraryAccess.Unrestricted).Search("road");
        var hits = Assert.IsAssignableFrom<List<PlaylistSummaryDto>>(((OkObjectResult)result.Result!).Value);

        Assert.Single(hits);
        Assert.Equal("Road Trip", hits[0].Name);
    }

    [Fact]
    public async Task Search_MatchesOnDescription()
    {
        await SeedNamedPlaylistAsync(_ownerId, "Mix", description: "songs for a long drive");

        var result = await NewController(_ownerId, LibraryAccess.Unrestricted).Search("drive");
        var hits = Assert.IsAssignableFrom<List<PlaylistSummaryDto>>(((OkObjectResult)result.Result!).Value);

        Assert.Single(hits);
    }

    [Fact]
    public async Task Search_HonoursVisibility()
    {
        await SeedNamedPlaylistAsync(_ownerId, "Secret Mix", isPublic: false);
        await SeedNamedPlaylistAsync(_ownerId, "Shared Mix", isPublic: true);

        var result = await NewController(_otherId, LibraryAccess.Unrestricted).Search("mix");
        var hits = Assert.IsAssignableFrom<List<PlaylistSummaryDto>>(((OkObjectResult)result.Result!).Value);

        Assert.Single(hits);
        Assert.Equal("Shared Mix", hits[0].Name);
        Assert.False(hits[0].IsOwner);
    }

    [Fact]
    public async Task Search_FindsSmartPlaylistsByName()
    {
        await SeedSmartPlaylistAsync(_ownerId, new SmartPlaylistRules()); // named "Auto Mix"

        var result = await NewController(_ownerId, LibraryAccess.Unrestricted).Search("auto");
        var hits = Assert.IsAssignableFrom<List<PlaylistSummaryDto>>(((OkObjectResult)result.Result!).Value);

        Assert.Single(hits);
        Assert.Equal(PlaylistKind.Smart, hits[0].Kind);
    }

    [Fact]
    public async Task Search_RanksNamePrefixHitsFirst()
    {
        await SeedNamedPlaylistAsync(_ownerId, "Evening Jazz");
        await SeedNamedPlaylistAsync(_ownerId, "Jazz Classics");

        var result = await NewController(_ownerId, LibraryAccess.Unrestricted).Search("jazz");
        var hits = Assert.IsAssignableFrom<List<PlaylistSummaryDto>>(((OkObjectResult)result.Result!).Value);

        Assert.Equal("Jazz Classics", hits[0].Name);
    }

    [Fact]
    public async Task Search_TreatsWildcardsAsLiteralText()
    {
        // Unescaped, "%" would match every playlist rather than the one named with it.
        await SeedNamedPlaylistAsync(_ownerId, "100% Bangers");
        await SeedNamedPlaylistAsync(_ownerId, "Something Else");

        var result = await NewController(_ownerId, LibraryAccess.Unrestricted).Search("0%");
        var hits = Assert.IsAssignableFrom<List<PlaylistSummaryDto>>(((OkObjectResult)result.Result!).Value);

        Assert.Single(hits);
        Assert.Equal("100% Bangers", hits[0].Name);
    }

    [Fact]
    public async Task Search_IgnoresQueriesShorterThanTwoCharacters()
    {
        await SeedNamedPlaylistAsync(_ownerId, "Road Trip");

        var result = await NewController(_ownerId, LibraryAccess.Unrestricted).Search("r");
        var hits = Assert.IsAssignableFrom<List<PlaylistSummaryDto>>(((OkObjectResult)result.Result!).Value);

        Assert.Empty(hits);
    }

    [Fact]
    public async Task Search_RespectsTheLimit()
    {
        for (var i = 0; i < 8; i++) await SeedNamedPlaylistAsync(_ownerId, $"Mix {i}");

        var result = await NewController(_ownerId, LibraryAccess.Unrestricted).Search("mix", limit: 3);
        var hits = Assert.IsAssignableFrom<List<PlaylistSummaryDto>>(((OkObjectResult)result.Result!).Value);

        Assert.Equal(3, hits.Count);
    }

    // ── Smart playlists ──────────────────────────────────────────────────────

    private async Task<Playlist> SeedSmartPlaylistAsync(Guid ownerId, SmartPlaylistRules rules)
    {
        var playlist = new Playlist
        {
            OwnerUserId = ownerId,
            Name = "Auto Mix",
            Kind = PlaylistKind.Smart,
            SmartRules = System.Text.Json.JsonSerializer.Serialize(
                rules, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)),
        };
        _db.Playlists.Add(playlist);
        await _db.SaveChangesAsync();
        return playlist;
    }

    [Fact]
    public async Task Create_WithRules_MakesASmartPlaylist()
    {
        var controller = NewController(_ownerId, LibraryAccess.Unrestricted);

        var result = await controller.Create(new CreatePlaylistRequest
        {
            Name = "Fresh",
            Rules = new SmartPlaylistRules { AddedWithinDays = 30 },
        });

        var dto = Assert.IsType<PlaylistSummaryDto>(Assert.IsType<CreatedAtActionResult>(result.Result).Value);
        Assert.Equal(PlaylistKind.Smart, dto.Kind);
        Assert.NotNull(dto.Rules);

        var stored = await _db.Playlists.FirstAsync(p => p.Id == dto.Id);
        Assert.Equal(PlaylistKind.Smart, stored.Kind);
        Assert.False(string.IsNullOrWhiteSpace(stored.SmartRules));
    }

    [Fact]
    public async Task Create_SmartPlaylistCannotBePublic()
    {
        // Membership is computed from the owner's favourites and listening, so a
        // public smart playlist would either expose those signals or mean something
        // different for every viewer.
        var controller = NewController(_ownerId, LibraryAccess.Unrestricted);

        var result = await controller.Create(new CreatePlaylistRequest
        {
            Name = "Shared?", IsPublic = true, Rules = new SmartPlaylistRules(),
        });

        Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.False(await _db.Playlists.AnyAsync(p => p.Name == "Shared?"));
    }

    [Fact]
    public async Task Create_RejectsContradictoryRules()
    {
        var controller = NewController(_ownerId, LibraryAccess.Unrestricted);

        var result = await controller.Create(new CreatePlaylistRequest
        {
            Name = "Impossible",
            Rules = new SmartPlaylistRules { FavoritesOnly = true, UnplayedOnly = true },
        });

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task Create_ClampsAnOversizedLimit()
    {
        var controller = NewController(_ownerId, LibraryAccess.Unrestricted);

        var result = await controller.Create(new CreatePlaylistRequest
        {
            Name = "Everything",
            Rules = new SmartPlaylistRules { Limit = 100_000 },
        });

        var dto = Assert.IsType<PlaylistSummaryDto>(Assert.IsType<CreatedAtActionResult>(result.Result).Value);
        Assert.Equal(SmartPlaylistRules.MaxLimit, dto.Rules!.Limit);
    }

    [Fact]
    public async Task Get_SmartPlaylist_ReturnsEvaluatedTracks()
    {
        // Rules match audio only; the seeded movie must not appear.
        var p = await SeedSmartPlaylistAsync(_ownerId, new SmartPlaylistRules());

        var result = await NewController(_ownerId, LibraryAccess.Unrestricted).Get(p.Id);

        var dto = Assert.IsType<PlaylistDetailDto>(((OkObjectResult)result.Result!).Value);
        Assert.Equal(PlaylistKind.Smart, dto.Kind);
        // Every audio track in the fixture, and neither the movie nor the albums.
        Assert.Equal(4, dto.Items.Count);
        Assert.All(dto.Items, e => Assert.NotEqual(_movieA.Id, e.Media.Id));
        Assert.All(dto.Items, e => Assert.NotEqual(_albumOne.Id, e.Media.Id));
        // No PlaylistItem rows back a smart playlist.
        Assert.False(await _db.PlaylistItems.AnyAsync(pi => pi.PlaylistId == p.Id));
    }

    [Fact]
    public async Task Get_SmartPlaylist_AppliesTheViewersAcl()
    {
        var p = await SeedSmartPlaylistAsync(_ownerId, new SmartPlaylistRules());

        var result = await NewController(_ownerId, LibraryAccess.AllowOnly(new[] { _libA.Id })).Get(p.Id);

        var dto = Assert.IsType<PlaylistDetailDto>(((OkObjectResult)result.Result!).Value);
        Assert.All(dto.Items, e => Assert.NotEqual(_libB.Id, e.Media.LibraryId));
    }

    [Fact]
    public async Task Get_SmartPlaylist_WithUnreadableRules_DegradesToEmpty()
    {
        // A hand-corrupted or downgraded rules blob should not 500 the page.
        var playlist = new Playlist
        {
            OwnerUserId = _ownerId, Name = "Broken",
            Kind = PlaylistKind.Smart, SmartRules = "{ not json",
        };
        _db.Playlists.Add(playlist);
        await _db.SaveChangesAsync();

        var result = await NewController(_ownerId, LibraryAccess.Unrestricted).Get(playlist.Id);

        var dto = Assert.IsType<PlaylistDetailDto>(((OkObjectResult)result.Result!).Value);
        Assert.Empty(dto.Items);
    }

    [Fact]
    public async Task List_SmartPlaylist_ReportsItsEvaluatedCount()
    {
        // Nothing is stored against the playlist, so a count of 0 would mean the
        // index never evaluated the rules.
        await SeedSmartPlaylistAsync(_ownerId, new SmartPlaylistRules());

        var result = await NewController(_ownerId, LibraryAccess.Unrestricted).List();
        var list = Assert.IsAssignableFrom<List<PlaylistSummaryDto>>(((OkObjectResult)result.Result!).Value);

        Assert.Equal(4, list[0].ItemCount);
    }

    [Fact]
    public async Task AddItems_RejectedOnASmartPlaylist()
    {
        var p = await SeedSmartPlaylistAsync(_ownerId, new SmartPlaylistRules());

        var result = await NewController(_ownerId, LibraryAccess.Unrestricted)
            .AddItems(p.Id, new AddPlaylistItemsRequest { MediaItemIds = new List<Guid> { _audioA1.Id } });

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.False(await _db.PlaylistItems.AnyAsync(pi => pi.PlaylistId == p.Id));
    }

    [Fact]
    public async Task RemoveItem_RejectedOnASmartPlaylist()
    {
        var p = await SeedSmartPlaylistAsync(_ownerId, new SmartPlaylistRules());

        var result = await NewController(_ownerId, LibraryAccess.Unrestricted)
            .RemoveItem(p.Id, Guid.NewGuid());

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task Reorder_RejectedOnASmartPlaylist()
    {
        var p = await SeedSmartPlaylistAsync(_ownerId, new SmartPlaylistRules());

        var result = await NewController(_ownerId, LibraryAccess.Unrestricted)
            .Reorder(p.Id, new ReorderPlaylistRequest { ItemIds = new List<Guid>() });

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task Update_CannotSetRulesOnAManualPlaylist()
    {
        // Converting kinds would either discard curated rows or strand them.
        var p = await SeedPlaylistAsync(_ownerId, isPublic: false, _audioA1.Id);

        var result = await NewController(_ownerId, LibraryAccess.Unrestricted)
            .Update(p.Id, new UpdatePlaylistRequest { Rules = new SmartPlaylistRules() });

        Assert.IsType<BadRequestObjectResult>(result);
        var stored = await _db.Playlists.FirstAsync(x => x.Id == p.Id);
        Assert.Equal(PlaylistKind.Manual, stored.Kind);
    }

    [Fact]
    public async Task Update_CannotMakeASmartPlaylistPublic()
    {
        var p = await SeedSmartPlaylistAsync(_ownerId, new SmartPlaylistRules());

        var result = await NewController(_ownerId, LibraryAccess.Unrestricted)
            .Update(p.Id, new UpdatePlaylistRequest { IsPublic = true });

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.False((await _db.Playlists.FirstAsync(x => x.Id == p.Id)).IsPublic);
    }

    [Fact]
    public async Task Update_InvalidRules_LeaveTheNameUntouched()
    {
        // Validation runs before any mutation, so a partly-invalid request is
        // rejected whole rather than persisting the half that parsed.
        var p = await SeedSmartPlaylistAsync(_ownerId, new SmartPlaylistRules());

        var result = await NewController(_ownerId, LibraryAccess.Unrestricted)
            .Update(p.Id, new UpdatePlaylistRequest
            {
                Name = "Renamed",
                Rules = new SmartPlaylistRules { FavoritesOnly = true, UnplayedOnly = true },
            });

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal("Auto Mix", (await _db.Playlists.FirstAsync(x => x.Id == p.Id)).Name);
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
        var covers = new Mock<SoftMedia.Server.Services.Media.IPlaylistCoverService>();
        var result = await NewController(_otherId, LibraryAccess.Unrestricted).Delete(p.Id, covers.Object);

        Assert.IsType<NotFoundResult>(result);
        Assert.True(await _db.Playlists.AnyAsync(x => x.Id == p.Id));
        // A refused delete must not reach for someone else's cover file either.
        covers.Verify(c => c.Delete(It.IsAny<Guid>()), Times.Never);
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
