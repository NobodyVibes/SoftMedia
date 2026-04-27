using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SoftMedia.Server.Controllers;
using SoftMedia.Server.Data;
using SoftMedia.Server.Models;
using SoftMedia.Server.Services.Abstractions;
using Xunit;

namespace SoftMedia.Server.Tests.Controllers;

/// <summary>
/// Coverage for the ER-023 bookmark endpoints on <see cref="BookController"/>.
/// Uses EF Core InMemory; mocks the other dependencies since bookmark CRUD
/// never calls through to them.
/// </summary>
public class BookControllerBookmarksTests : IDisposable
{
    private readonly AppDbContext _context;
    private readonly Guid _userId = Guid.NewGuid();
    private readonly Guid _otherUserId = Guid.NewGuid();
    private readonly Guid _mediaId = Guid.NewGuid();

    public BookControllerBookmarksTests()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"bookmarks-{Guid.NewGuid()}")
            .Options;
        _context = new AppDbContext(options);

        _context.MediaItems.Add(new MediaItem
        {
            Id = _mediaId,
            Title = "Test Book",
            Type = MediaType.Book,
            Path = "/lib/test.epub",
            LibraryId = Guid.NewGuid(),
        });
        _context.SaveChanges();
    }

    public void Dispose() => _context.Dispose();

    private BookController NewController(Guid? asUser = null)
    {
        var controller = new BookController(
            Mock.Of<IMediaRepository>(),
            Mock.Of<IStreamSecurityService>(),
            Mock.Of<IComicArchiveService>(),
            Mock.Of<IComicPageThumbnailService>(),
            _context,
            NullLogger<BookController>.Instance);
        var identity = new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, (asUser ?? _userId).ToString()),
        });
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) }
        };
        return controller;
    }

    [Fact]
    public async Task CreateBookmark_PersistsRowAndReturnsDto()
    {
        var controller = NewController();
        var result = await controller.CreateBookmark(_mediaId, new CreateBookmarkRequest
        {
            Position = 42,
            Label = "Favourite quote",
        });

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsType<BookmarkDto>(ok.Value);
        Assert.NotEqual(Guid.Empty, dto.Id);
        Assert.Equal(42, dto.Position);
        Assert.Equal("Favourite quote", dto.Label);

        Assert.Single(_context.Bookmarks);
    }

    [Fact]
    public async Task CreateBookmark_AcceptsCfiWithoutPosition()
    {
        var controller = NewController();
        var result = await controller.CreateBookmark(_mediaId, new CreateBookmarkRequest
        {
            Cfi = "epubcfi(/6/4!/4[chap01]/2/2/3:0)",
        });

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsType<BookmarkDto>(ok.Value);
        Assert.Null(dto.Position);
        Assert.Contains("epubcfi", dto.Cfi);
    }

    [Fact]
    public async Task CreateBookmark_RejectsEmptyRequest()
    {
        var controller = NewController();
        var result = await controller.CreateBookmark(_mediaId, new CreateBookmarkRequest());

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task CreateBookmark_RejectsNonPositivePosition()
    {
        var controller = NewController();
        var result = await controller.CreateBookmark(_mediaId, new CreateBookmarkRequest
        {
            Position = 0,
        });

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task CreateBookmark_NotFoundForUnknownMedia()
    {
        var controller = NewController();
        var result = await controller.CreateBookmark(Guid.NewGuid(), new CreateBookmarkRequest
        {
            Position = 1,
        });

        Assert.IsType<NotFoundResult>(result.Result);
    }

    [Fact]
    public async Task ListBookmarks_ReturnsOnlyCallersRows()
    {
        _context.Bookmarks.AddRange(
            new Bookmark { Id = Guid.NewGuid(), UserId = _userId, MediaItemId = _mediaId, Position = 1 },
            new Bookmark { Id = Guid.NewGuid(), UserId = _userId, MediaItemId = _mediaId, Position = 2 },
            new Bookmark { Id = Guid.NewGuid(), UserId = _otherUserId, MediaItemId = _mediaId, Position = 3 }
        );
        _context.SaveChanges();

        var controller = NewController();
        var result = await controller.ListBookmarks(_mediaId);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var list = Assert.IsType<List<BookmarkDto>>(ok.Value);
        Assert.Equal(2, list.Count);
        Assert.DoesNotContain(list, b => b.Position == 3);
    }

    [Fact]
    public async Task UpdateBookmark_RelabelsCallersRow()
    {
        var bookmark = new Bookmark
        {
            Id = Guid.NewGuid(),
            UserId = _userId,
            MediaItemId = _mediaId,
            Position = 1,
            Label = "old",
        };
        _context.Bookmarks.Add(bookmark);
        _context.SaveChanges();

        var controller = NewController();
        var result = await controller.UpdateBookmark(_mediaId, bookmark.Id, new UpdateBookmarkRequest
        {
            Label = "new label",
        });

        Assert.IsType<NoContentResult>(result);
        var refreshed = _context.Bookmarks.Single(b => b.Id == bookmark.Id);
        Assert.Equal("new label", refreshed.Label);
    }

    [Fact]
    public async Task UpdateBookmark_RefusesOtherUsersRow()
    {
        var bookmark = new Bookmark
        {
            Id = Guid.NewGuid(),
            UserId = _otherUserId,
            MediaItemId = _mediaId,
            Position = 1,
        };
        _context.Bookmarks.Add(bookmark);
        _context.SaveChanges();

        var controller = NewController();
        var result = await controller.UpdateBookmark(_mediaId, bookmark.Id, new UpdateBookmarkRequest
        {
            Label = "hijack",
        });

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task DeleteBookmark_RemovesCallersRow()
    {
        var bookmark = new Bookmark
        {
            Id = Guid.NewGuid(),
            UserId = _userId,
            MediaItemId = _mediaId,
            Position = 1,
        };
        _context.Bookmarks.Add(bookmark);
        _context.SaveChanges();

        var controller = NewController();
        var result = await controller.DeleteBookmark(_mediaId, bookmark.Id);

        Assert.IsType<NoContentResult>(result);
        Assert.False(_context.Bookmarks.Any());
    }

    [Fact]
    public async Task DeleteBookmark_RefusesOtherUsersRow()
    {
        var bookmark = new Bookmark
        {
            Id = Guid.NewGuid(),
            UserId = _otherUserId,
            MediaItemId = _mediaId,
            Position = 1,
        };
        _context.Bookmarks.Add(bookmark);
        _context.SaveChanges();

        var controller = NewController();
        var result = await controller.DeleteBookmark(_mediaId, bookmark.Id);

        Assert.IsType<NotFoundResult>(result);
        Assert.Single(_context.Bookmarks);
    }
}
