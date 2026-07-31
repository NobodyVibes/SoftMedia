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

/// Wave E2 — CollectionsController coverage.
public class CollectionsControllerTests : IDisposable
{
    private readonly AppDbContext _db;
    private readonly Library _libA;
    private readonly Library _libB;
    private readonly Collection _autoCollection;
    private readonly Collection _manualCollection;
    private readonly MediaItem _movie1;
    private readonly MediaItem _movie2;
    private readonly MediaItem _movie3;
    private readonly MediaItem _movieB1;

    public CollectionsControllerTests()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"collctlr-{Guid.NewGuid()}")
            .Options;
        _db = new AppDbContext(options);

        _libA = new Library { Id = Guid.NewGuid(), Name = "A", Type = LibraryType.Movie, Paths = new() { "/a" } };
        _libB = new Library { Id = Guid.NewGuid(), Name = "B", Type = LibraryType.Movie, Paths = new() { "/b" } };
        _autoCollection = new Collection { Id = Guid.NewGuid(), Name = "Lord of the Rings", WikidataId = "Q170461" };
        _manualCollection = new Collection { Id = Guid.NewGuid(), Name = "My Curated Set", WikidataId = null };

        _movie1 = new MediaItem
        {
            Id = Guid.NewGuid(), Type = MediaType.Movie, Title = "FotR", SortTitle = "FotR",
            Path = "/a/fotr.mkv", LibraryId = _libA.Id, CollectionId = _autoCollection.Id,
            Year = 2001, ReleaseDate = new DateTime(2001, 12, 19, 0, 0, 0, DateTimeKind.Utc),
        };
        _movie2 = new MediaItem
        {
            Id = Guid.NewGuid(), Type = MediaType.Movie, Title = "TT", SortTitle = "TT",
            Path = "/a/tt.mkv", LibraryId = _libA.Id, CollectionId = _autoCollection.Id,
            Year = 2002, ReleaseDate = new DateTime(2002, 12, 18, 0, 0, 0, DateTimeKind.Utc),
        };
        _movie3 = new MediaItem
        {
            Id = Guid.NewGuid(), Type = MediaType.Movie, Title = "RotK", SortTitle = "RotK",
            Path = "/a/rotk.mkv", LibraryId = _libA.Id, CollectionId = _autoCollection.Id,
            Year = 2003, ReleaseDate = new DateTime(2003, 12, 17, 0, 0, 0, DateTimeKind.Utc),
        };
        // Movie in libB, attached to manual collection only.
        _movieB1 = new MediaItem
        {
            Id = Guid.NewGuid(), Type = MediaType.Movie, Title = "Solo", SortTitle = "Solo",
            Path = "/b/solo.mkv", LibraryId = _libB.Id, CollectionId = _manualCollection.Id,
            Year = 2018,
        };

        _db.Libraries.AddRange(_libA, _libB);
        _db.Collections.AddRange(_autoCollection, _manualCollection);
        _db.MediaItems.AddRange(_movie1, _movie2, _movie3, _movieB1);
        _db.SaveChanges();
    }

    public void Dispose() => _db.Dispose();

    // ── DV-WI-016 follow-up: duplicate file copies are ONE collection entry ──

    private MediaItem AddDuplicateOf(MediaItem original, string path)
    {
        var group = original.VersionGroupId ?? Guid.NewGuid();
        original.VersionGroupId = group;
        var duplicate = new MediaItem
        {
            Id = Guid.NewGuid(), Type = MediaType.Movie, Title = original.Title,
            SortTitle = original.SortTitle, Path = path, LibraryId = original.LibraryId,
            CollectionId = original.CollectionId, Year = original.Year,
            ReleaseDate = original.ReleaseDate, VersionGroupId = group,
        };
        _db.MediaItems.Add(duplicate);
        _db.SaveChanges();
        return duplicate;
    }

    [Fact]
    public async Task ByMovie_DuplicateCopiesCollapse_AndCurrentMarksTheViewedCopy()
    {
        var duplicate = AddDuplicateOf(_movie2, "/a/tt-4k.mkv");

        // Viewing FotR: "TT" appears ONCE in the strip, not once per file.
        var result = await NewController(LibraryAccess.Unrestricted).GetByMovie(_movie1.Id);
        var dto = Assert.IsType<CollectionDetailDto>(((OkObjectResult)result).Value);
        var entry = Assert.Single(dto.Items, e => e.Media.Title == "TT");

        // Viewing the DUPLICATE copy: its group is represented by the viewed copy
        // itself, so the "now viewing" highlight matches.
        var result2 = await NewController(LibraryAccess.Unrestricted).GetByMovie(duplicate.Id);
        var dto2 = Assert.IsType<CollectionDetailDto>(((OkObjectResult)result2).Value);
        var ttEntry = Assert.Single(dto2.Items, e => e.Media.Title == "TT");
        Assert.Equal(duplicate.Id, ttEntry.Media.Id);
        Assert.True(ttEntry.IsCurrent);
        Assert.DoesNotContain(dto2.Items, e => e.Media.Id == _movie2.Id);
    }

    [Fact]
    public async Task List_OneMovieDuplicated_IsNotATwoMovieCollection()
    {
        // The manual collection holds ONE film as two files — it must stay hidden
        // (threshold counts logical titles), while LotR still lists three.
        AddDuplicateOf(_movieB1, "/b/solo-4k.mkv");

        var result = await NewController(LibraryAccess.Unrestricted).List();
        var dtos = Assert.IsType<List<CollectionSummaryDto>>(((OkObjectResult)result.Result!).Value);

        Assert.DoesNotContain(dtos, c => c.Id == _manualCollection.Id);
        var lotr = Assert.Single(dtos, c => c.Id == _autoCollection.Id);
        Assert.Equal(3, lotr.VisibleItemCount);
    }

    private CollectionsController NewController(LibraryAccess access)
    {
        var libraryAccess = new Mock<IUserLibraryAccessProvider>();
        libraryAccess.Setup(p => p.GetCurrentAsync()).ReturnsAsync(access);
        var ratings = new Mock<SoftMedia.Server.Services.Security.ContentRating.IUserContentRatingProvider>();
        ratings.Setup(p => p.GetCurrentAsync())
            .ReturnsAsync(SoftMedia.Server.Services.Security.ContentRating.UserRatingCeilings.Unrestricted);

        var controller = new CollectionsController(
            _db, libraryAccess.Object, ratings.Object, NullLogger<CollectionsController>.Instance);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext(),
        };
        return controller;
    }

    // ── List ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task List_OnlyShowsCollectionsWith2OrMoreVisibleItems()
    {
        // _autoCollection has 3 items in libA; _manualCollection has 1 item in libB.
        // With unrestricted access, the auto qualifies; manual does not.
        var result = await NewController(LibraryAccess.Unrestricted).List();
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var list = Assert.IsAssignableFrom<List<CollectionSummaryDto>>(ok.Value);

        Assert.Single(list);
        Assert.Equal(_autoCollection.Id, list[0].Id);
        Assert.True(list[0].IsAuto);
        Assert.Equal(3, list[0].VisibleItemCount);
    }

    [Fact]
    public async Task List_RespectsAcl_HidesCollectionsThatBecomeUnderpopulated()
    {
        // Restrict to libB only — auto collection (in libA) drops to 0 visible.
        var result = await NewController(LibraryAccess.AllowOnly(new[] { _libB.Id })).List();
        var list = Assert.IsAssignableFrom<List<CollectionSummaryDto>>(((OkObjectResult)result.Result!).Value);

        Assert.Empty(list); // _manualCollection has only 1 item; below the ≥2 threshold.
    }

    // ── Detail ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Get_AutoCollection_ReturnsItemsOrderedByReleaseDate()
    {
        var result = await NewController(LibraryAccess.Unrestricted).Get(_autoCollection.Id);
        var dto = Assert.IsType<CollectionDetailDto>(((OkObjectResult)result.Result!).Value);

        Assert.True(dto.IsAuto);
        Assert.Equal(3, dto.Items.Count);
        Assert.Equal(_movie1.Id, dto.Items[0].Media.Id); // 2001
        Assert.Equal(_movie2.Id, dto.Items[1].Media.Id); // 2002
        Assert.Equal(_movie3.Id, dto.Items[2].Media.Id); // 2003
    }

    [Fact]
    public async Task Get_AclHidesAllItems_Returns404()
    {
        var result = await NewController(LibraryAccess.AllowOnly(new[] { _libB.Id })).Get(_autoCollection.Id);
        Assert.IsType<NotFoundResult>(result.Result);
    }

    // ── By-movie strip ──────────────────────────────────────────────────────

    [Fact]
    public async Task GetByMovie_MarksCurrentMovie()
    {
        var result = await NewController(LibraryAccess.Unrestricted).GetByMovie(_movie2.Id);
        var ok = Assert.IsType<OkObjectResult>(result);
        var dto = Assert.IsType<CollectionDetailDto>(ok.Value);

        Assert.Equal(3, dto.Items.Count);
        Assert.Equal(1, dto.Items.Count(i => i.IsCurrent));
        Assert.True(dto.Items.First(i => i.Media.Id == _movie2.Id).IsCurrent);
    }

    [Fact]
    public async Task GetByMovie_MovieWithoutCollection_Returns204()
    {
        var orphan = new MediaItem
        {
            Id = Guid.NewGuid(), Type = MediaType.Movie, Title = "Solo Mov", SortTitle = "Solo Mov",
            Path = "/a/orphan.mkv", LibraryId = _libA.Id,
        };
        _db.MediaItems.Add(orphan);
        await _db.SaveChangesAsync();

        var result = await NewController(LibraryAccess.Unrestricted).GetByMovie(orphan.Id);
        Assert.IsType<NoContentResult>(result);
    }

    [Fact]
    public async Task GetByMovie_LessThan2VisibleSiblings_Returns204()
    {
        // Restrict so only _movie1 is visible — strip rule is ≥2 siblings.
        var library = new Library { Id = Guid.NewGuid(), Name = "Solo", Type = LibraryType.Movie, Paths = new() { "/c" } };
        _db.Libraries.Add(library);
        var solo = new MediaItem
        {
            Id = Guid.NewGuid(), Type = MediaType.Movie, Title = "S", SortTitle = "S",
            Path = "/c/s.mkv", LibraryId = library.Id, CollectionId = _autoCollection.Id, Year = 2024,
        };
        _db.MediaItems.Add(solo);
        await _db.SaveChangesAsync();

        var result = await NewController(LibraryAccess.AllowOnly(new[] { library.Id })).GetByMovie(solo.Id);
        Assert.IsType<NoContentResult>(result);
    }

    [Fact]
    public async Task GetByMovie_BlockedByAcl_Returns404()
    {
        var result = await NewController(LibraryAccess.AllowOnly(new[] { _libB.Id })).GetByMovie(_movie1.Id);
        Assert.IsType<NotFoundResult>(result);
    }

    // ── Manual create / edit / delete ────────────────────────────────────────

    [Fact]
    public async Task Update_AutoCollection_Rejected()
    {
        var result = await NewController(LibraryAccess.Unrestricted)
            .Update(_autoCollection.Id, new UpdateCollectionRequest { Name = "Hijacked" });

        Assert.IsType<BadRequestObjectResult>(result);
        var refreshed = await _db.Collections.FirstAsync(c => c.Id == _autoCollection.Id);
        Assert.Equal("Lord of the Rings", refreshed.Name);
    }

    [Fact]
    public async Task Update_ManualCollection_Succeeds()
    {
        var result = await NewController(LibraryAccess.Unrestricted)
            .Update(_manualCollection.Id, new UpdateCollectionRequest { Name = "Tarantino" });

        Assert.IsType<NoContentResult>(result);
        var refreshed = await _db.Collections.FirstAsync(c => c.Id == _manualCollection.Id);
        Assert.Equal("Tarantino", refreshed.Name);
    }

    [Fact]
    public async Task Delete_AutoCollection_Rejected()
    {
        var result = await NewController(LibraryAccess.Unrestricted).Delete(_autoCollection.Id);
        Assert.IsType<BadRequestObjectResult>(result);
        Assert.True(await _db.Collections.AnyAsync(c => c.Id == _autoCollection.Id));
    }

    [Fact]
    public async Task Create_PopulatesCollectionAndAttachesMovies()
    {
        var movie = new MediaItem
        {
            Id = Guid.NewGuid(), Type = MediaType.Movie, Title = "Indie", SortTitle = "Indie",
            Path = "/a/indie.mkv", LibraryId = _libA.Id,
        };
        _db.MediaItems.Add(movie);
        await _db.SaveChangesAsync();

        var result = await NewController(LibraryAccess.Unrestricted)
            .Create(new CreateCollectionRequest
            {
                Name = "Indie Picks",
                MovieIds = new List<Guid> { movie.Id },
            });

        var created = Assert.IsType<CreatedAtActionResult>(result.Result);
        var dto = Assert.IsType<CollectionSummaryDto>(created.Value);
        Assert.False(dto.IsAuto);

        var refreshed = await _db.MediaItems.FirstAsync(m => m.Id == movie.Id);
        Assert.Equal(dto.Id, refreshed.CollectionId);
    }

    [Fact]
    public async Task RemoveItem_ManualOnly_DetachesMovie()
    {
        var result = await NewController(LibraryAccess.Unrestricted)
            .RemoveItem(_manualCollection.Id, _movieB1.Id);
        Assert.IsType<NoContentResult>(result);

        var refreshed = await _db.MediaItems.FirstAsync(m => m.Id == _movieB1.Id);
        Assert.Null(refreshed.CollectionId);
    }
}
