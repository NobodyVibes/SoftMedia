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
/// Coverage for ER-032's <c>GET /books/{id}/thumbnail/{pageNumber}</c> endpoint.
/// Exercises access checks, size-preset routing, format gating (CBZ/CBR only),
/// and the null-bytes → 404 path.
/// </summary>
public class BookControllerThumbnailTests : IDisposable
{
    private readonly Mock<IMediaRepository> _repo = new();
    private readonly Mock<IStreamSecurityService> _security = new();
    private readonly Mock<IComicArchiveService> _comic = new();
    private readonly Mock<IComicPageThumbnailService> _thumbs = new();
    private readonly AppDbContext _context;

    public BookControllerThumbnailTests()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"thumb-{Guid.NewGuid()}")
            .Options;
        _context = new AppDbContext(options);
    }

    public void Dispose() => _context.Dispose();

    private BookController NewController()
    {
        var controller = new BookController(
            _repo.Object, _security.Object, _comic.Object, _thumbs.Object, _context,
            NullLogger<BookController>.Instance);
        // The thumbnail endpoint writes a Cache-Control response header; that
        // requires a live HttpContext. The other endpoints in this controller
        // don't touch Response, so only this test class provides one.
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext(),
        };
        return controller;
    }

    private static MediaItem Item(string ext) => new()
    {
        Id = Guid.NewGuid(),
        Path = $@"C:\lib\book{ext}",
        Type = MediaType.Book,
        Library = new Library { Paths = new List<string> { @"C:\lib" } }
    };

    [Fact]
    public async Task GetThumbnail_ReturnsJpegForCbzPage()
    {
        var item = Item(".cbz");
        _repo.Setup(r => r.GetByIdWithLibraryAsync(item.Id)).ReturnsAsync(item);
        _security.Setup(s => s.ValidateMediaAccessAsync(item)).ReturnsAsync(MediaAccessResult.Allowed);
        _comic.Setup(c => c.IsSupportedArchive(item.Path)).Returns(true);
        _thumbs.Setup(t => t.GetAsync(item.Path, 3, 160, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new byte[] { 1, 2, 3 });

        var result = await NewController().GetThumbnail(item.Id, 3, "sm", CancellationToken.None);

        var file = Assert.IsType<FileContentResult>(result);
        Assert.Equal("image/jpeg", file.ContentType);
        Assert.Equal(3, file.FileContents.Length);
    }

    [Fact]
    public async Task GetThumbnail_RoutesSizePresetsToExpectedWidths()
    {
        var item = Item(".cbz");
        _repo.Setup(r => r.GetByIdWithLibraryAsync(item.Id)).ReturnsAsync(item);
        _security.Setup(s => s.ValidateMediaAccessAsync(item)).ReturnsAsync(MediaAccessResult.Allowed);
        _comic.Setup(c => c.IsSupportedArchive(item.Path)).Returns(true);
        _thumbs.Setup(t => t.GetAsync(item.Path, 1, It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new byte[] { 0 });

        await NewController().GetThumbnail(item.Id, 1, "md", CancellationToken.None);
        _thumbs.Verify(t => t.GetAsync(item.Path, 1, 240, It.IsAny<CancellationToken>()), Times.Once);

        await NewController().GetThumbnail(item.Id, 1, "lg", CancellationToken.None);
        _thumbs.Verify(t => t.GetAsync(item.Path, 1, 360, It.IsAny<CancellationToken>()), Times.Once);

        // Unknown / missing size falls back to sm (160).
        await NewController().GetThumbnail(item.Id, 1, "garbage", CancellationToken.None);
        _thumbs.Verify(t => t.GetAsync(item.Path, 1, 160, It.IsAny<CancellationToken>()), Times.AtLeastOnce);
    }

    [Fact]
    public async Task GetThumbnail_BadRequestForPdf()
    {
        var item = Item(".pdf");
        _repo.Setup(r => r.GetByIdWithLibraryAsync(item.Id)).ReturnsAsync(item);
        _security.Setup(s => s.ValidateMediaAccessAsync(item)).ReturnsAsync(MediaAccessResult.Allowed);
        _comic.Setup(c => c.IsSupportedArchive(item.Path)).Returns(false);

        var result = await NewController().GetThumbnail(item.Id, 1, null, CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task GetThumbnail_RejectsNonPositivePage()
    {
        var result = await NewController().GetThumbnail(Guid.NewGuid(), 0, null, CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task GetThumbnail_NotFoundWhenThumbnailNull()
    {
        var item = Item(".cbz");
        _repo.Setup(r => r.GetByIdWithLibraryAsync(item.Id)).ReturnsAsync(item);
        _security.Setup(s => s.ValidateMediaAccessAsync(item)).ReturnsAsync(MediaAccessResult.Allowed);
        _comic.Setup(c => c.IsSupportedArchive(item.Path)).Returns(true);
        _thumbs.Setup(t => t.GetAsync(item.Path, 99, It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((byte[]?)null);

        var result = await NewController().GetThumbnail(item.Id, 99, null, CancellationToken.None);
        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task GetThumbnail_ReturnsNotFoundForUnauthorisedAccess()
    {
        // Wave C — Unauthorized maps to 404 (anti-probe per SDD §6.2).
        var item = Item(".cbz");
        _repo.Setup(r => r.GetByIdWithLibraryAsync(item.Id)).ReturnsAsync(item);
        _security.Setup(s => s.ValidateMediaAccessAsync(item)).ReturnsAsync(MediaAccessResult.Unauthorized);

        var result = await NewController().GetThumbnail(item.Id, 1, null, CancellationToken.None);
        Assert.IsType<NotFoundResult>(result);
    }
}
