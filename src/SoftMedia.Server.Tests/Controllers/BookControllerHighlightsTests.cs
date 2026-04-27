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
/// Coverage for the ER-040 / ER-041 highlight endpoints on <see cref="BookController"/>.
/// </summary>
public class BookControllerHighlightsTests : IDisposable
{
    private readonly AppDbContext _context;
    private readonly Guid _userId = Guid.NewGuid();
    private readonly Guid _otherUserId = Guid.NewGuid();
    private readonly Guid _mediaId = Guid.NewGuid();

    public BookControllerHighlightsTests()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"highlights-{Guid.NewGuid()}")
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
    public async Task CreateHighlight_PersistsAndReturnsDto()
    {
        var controller = NewController();
        var result = await controller.CreateHighlight(_mediaId, new CreateHighlightRequest
        {
            LocationJson = "{\"type\":\"epub\",\"cfi\":\"epubcfi(/6/4)\"}",
            Colour = "yellow",
            QuotedText = "The quick brown fox",
        });

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsType<HighlightDto>(ok.Value);
        Assert.Equal("yellow", dto.Colour);
        Assert.Contains("brown fox", dto.QuotedText);
        Assert.Single(_context.Highlights);
    }

    [Fact]
    public async Task CreateHighlight_RequiresLocationJson()
    {
        var controller = NewController();
        var result = await controller.CreateHighlight(_mediaId, new CreateHighlightRequest
        {
            LocationJson = "",
            QuotedText = "x",
        });

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task CreateHighlight_RejectsOversizedNote()
    {
        var controller = NewController();
        var result = await controller.CreateHighlight(_mediaId, new CreateHighlightRequest
        {
            LocationJson = "{\"type\":\"epub\",\"cfi\":\"x\"}",
            QuotedText = "q",
            Note = new string('n', 9000),
        });

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task CreateHighlight_NotFoundForUnknownMedia()
    {
        var controller = NewController();
        var result = await controller.CreateHighlight(Guid.NewGuid(), new CreateHighlightRequest
        {
            LocationJson = "{\"type\":\"epub\",\"cfi\":\"x\"}",
            QuotedText = "q",
        });

        Assert.IsType<NotFoundResult>(result.Result);
    }

    [Fact]
    public async Task ListHighlights_ReturnsOnlyCallersRows()
    {
        _context.Highlights.AddRange(
            new Highlight { UserId = _userId, MediaItemId = _mediaId, LocationJson = "{}", QuotedText = "mine 1" },
            new Highlight { UserId = _userId, MediaItemId = _mediaId, LocationJson = "{}", QuotedText = "mine 2" },
            new Highlight { UserId = _otherUserId, MediaItemId = _mediaId, LocationJson = "{}", QuotedText = "theirs" }
        );
        _context.SaveChanges();

        var controller = NewController();
        var result = await controller.ListHighlights(_mediaId);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var list = Assert.IsType<List<HighlightDto>>(ok.Value);
        Assert.Equal(2, list.Count);
        Assert.All(list, h => Assert.StartsWith("mine", h.QuotedText));
    }

    [Fact]
    public async Task UpdateHighlight_PatchesColourAndNote()
    {
        var h = new Highlight
        {
            Id = Guid.NewGuid(),
            UserId = _userId,
            MediaItemId = _mediaId,
            LocationJson = "{}",
            Colour = "yellow",
            QuotedText = "t",
        };
        _context.Highlights.Add(h);
        _context.SaveChanges();

        var controller = NewController();
        var result = await controller.UpdateHighlight(_mediaId, h.Id, new UpdateHighlightRequest
        {
            Colour = "green",
            Note = "remember this for ER-041",
        });

        Assert.IsType<NoContentResult>(result);
        var updated = _context.Highlights.Single(x => x.Id == h.Id);
        Assert.Equal("green", updated.Colour);
        Assert.Equal("remember this for ER-041", updated.Note);
    }

    [Fact]
    public async Task UpdateHighlight_RefusesOtherUsersRow()
    {
        var h = new Highlight
        {
            Id = Guid.NewGuid(),
            UserId = _otherUserId,
            MediaItemId = _mediaId,
            LocationJson = "{}",
            QuotedText = "t",
        };
        _context.Highlights.Add(h);
        _context.SaveChanges();

        var controller = NewController();
        var result = await controller.UpdateHighlight(_mediaId, h.Id, new UpdateHighlightRequest
        {
            Colour = "hijack",
        });

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task DeleteHighlight_RemovesCallersRow()
    {
        var h = new Highlight
        {
            Id = Guid.NewGuid(),
            UserId = _userId,
            MediaItemId = _mediaId,
            LocationJson = "{}",
            QuotedText = "t",
        };
        _context.Highlights.Add(h);
        _context.SaveChanges();

        var controller = NewController();
        var result = await controller.DeleteHighlight(_mediaId, h.Id);

        Assert.IsType<NoContentResult>(result);
        Assert.False(_context.Highlights.Any());
    }

    [Fact]
    public async Task DeleteHighlight_RefusesOtherUsersRow()
    {
        var h = new Highlight
        {
            Id = Guid.NewGuid(),
            UserId = _otherUserId,
            MediaItemId = _mediaId,
            LocationJson = "{}",
            QuotedText = "t",
        };
        _context.Highlights.Add(h);
        _context.SaveChanges();

        var controller = NewController();
        var result = await controller.DeleteHighlight(_mediaId, h.Id);

        Assert.IsType<NotFoundResult>(result);
        Assert.Single(_context.Highlights);
    }
}
