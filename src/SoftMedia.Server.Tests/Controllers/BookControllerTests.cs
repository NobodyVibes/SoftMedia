using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SoftMedia.Server.Controllers;
using SoftMedia.Server.Data;
using SoftMedia.Server.DTOs;
using SoftMedia.Server.Models;
using SoftMedia.Server.Services.Abstractions;
using Xunit;

namespace SoftMedia.Server.Tests.Controllers;

public class BookControllerTests
{
    private readonly Mock<IMediaRepository> _repo = new();
    private readonly Mock<IStreamSecurityService> _security = new();
    private readonly Mock<IComicArchiveService> _comic = new();

    private BookController NewController()
    {
        // ER-023 / ER-032: the controller now takes an AppDbContext for
        // bookmark/highlight CRUD and an IComicPageThumbnailService for
        // scrubber previews. The tests in this file target the info / page
        // endpoints which don't touch either, so a fresh throwaway InMemory
        // context and a Mock.Of thumbnail service are fine.
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"bookctl-{Guid.NewGuid()}")
            .Options;
        var context = new AppDbContext(options);
        return new BookController(
            _repo.Object,
            _security.Object,
            _comic.Object,
            Moq.Mock.Of<SoftMedia.Server.Services.Abstractions.IComicPageThumbnailService>(),
            context,
            NullLogger<BookController>.Instance);
    }

    private static MediaItem Item(string ext) => new()
    {
        Id = Guid.NewGuid(),
        Path = $@"C:\lib\book{ext}",
        Type = MediaType.Book,
        Library = new Library { Paths = new List<string> { @"C:\lib" } }
    };

    [Fact]
    public async Task GetInfo_ReturnsPageCountForCbz()
    {
        var item = Item(".cbz");
        _repo.Setup(r => r.GetByIdWithLibraryAsync(item.Id)).ReturnsAsync(item);
        _security.Setup(s => s.ValidateMediaAccess(item)).Returns(MediaAccessResult.Allowed);
        _comic.Setup(c => c.IsSupportedArchive(item.Path)).Returns(true);
        _comic.Setup(c => c.GetPageCountAsync(item.Path, It.IsAny<CancellationToken>())).ReturnsAsync(42);

        var result = await NewController().GetInfo(item.Id, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsType<BookInfoDto>(ok.Value);
        Assert.Equal("cbz", dto.Format);
        Assert.Equal(42, dto.PageCount);
    }

    [Fact]
    public async Task GetInfo_OmitsPageCountForPdf()
    {
        var item = Item(".pdf");
        _repo.Setup(r => r.GetByIdWithLibraryAsync(item.Id)).ReturnsAsync(item);
        _security.Setup(s => s.ValidateMediaAccess(item)).Returns(MediaAccessResult.Allowed);
        _comic.Setup(c => c.IsSupportedArchive(item.Path)).Returns(false);

        var result = await NewController().GetInfo(item.Id, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsType<BookInfoDto>(ok.Value);
        Assert.Equal("pdf", dto.Format);
        Assert.Null(dto.PageCount);
    }

    [Fact]
    public async Task GetInfo_ReturnsNotFoundWhenMissing()
    {
        var id = Guid.NewGuid();
        _repo.Setup(r => r.GetByIdWithLibraryAsync(id)).ReturnsAsync((MediaItem?)null);
        _security.Setup(s => s.ValidateMediaAccess(It.IsAny<MediaItem>())).Returns(MediaAccessResult.FileNotFound);

        var result = await NewController().GetInfo(id, CancellationToken.None);

        Assert.IsType<NotFoundResult>(result.Result);
    }

    [Fact]
    public async Task GetInfo_ForbidsUnauthorizedAccess()
    {
        var item = Item(".cbz");
        _repo.Setup(r => r.GetByIdWithLibraryAsync(item.Id)).ReturnsAsync(item);
        _security.Setup(s => s.ValidateMediaAccess(item)).Returns(MediaAccessResult.Unauthorized);

        var result = await NewController().GetInfo(item.Id, CancellationToken.None);

        Assert.IsType<ForbidResult>(result.Result);
    }

    [Fact]
    public async Task GetPage_ReturnsImageFileForValidCbz()
    {
        var item = Item(".cbz");
        _repo.Setup(r => r.GetByIdWithLibraryAsync(item.Id)).ReturnsAsync(item);
        _security.Setup(s => s.ValidateMediaAccess(item)).Returns(MediaAccessResult.Allowed);
        _comic.Setup(c => c.IsSupportedArchive(item.Path)).Returns(true);
        _comic.Setup(c => c.GetPageAsync(item.Path, 3, It.IsAny<CancellationToken>()))
              .ReturnsAsync(new ComicPage(new byte[] { 9, 9, 9 }, "image/png"));

        var result = await NewController().GetPage(item.Id, 3, CancellationToken.None);

        var file = Assert.IsType<FileContentResult>(result);
        Assert.Equal("image/png", file.ContentType);
        Assert.Equal(new byte[] { 9, 9, 9 }, file.FileContents);
    }

    [Fact]
    public async Task GetPage_Returns404ForOutOfRange()
    {
        var item = Item(".cbz");
        _repo.Setup(r => r.GetByIdWithLibraryAsync(item.Id)).ReturnsAsync(item);
        _security.Setup(s => s.ValidateMediaAccess(item)).Returns(MediaAccessResult.Allowed);
        _comic.Setup(c => c.IsSupportedArchive(item.Path)).Returns(true);
        _comic.Setup(c => c.GetPageAsync(item.Path, 999, It.IsAny<CancellationToken>()))
              .ReturnsAsync((ComicPage?)null);

        var result = await NewController().GetPage(item.Id, 999, CancellationToken.None);

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task GetPage_Returns400ForNonComicFormat()
    {
        var item = Item(".pdf");
        _repo.Setup(r => r.GetByIdWithLibraryAsync(item.Id)).ReturnsAsync(item);
        _security.Setup(s => s.ValidateMediaAccess(item)).Returns(MediaAccessResult.Allowed);
        _comic.Setup(c => c.IsSupportedArchive(item.Path)).Returns(false);

        var result = await NewController().GetPage(item.Id, 1, CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task GetPage_Returns400ForPageZero()
    {
        var result = await NewController().GetPage(Guid.NewGuid(), 0, CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task GetPage_ForbidsUnauthorizedAccess()
    {
        var item = Item(".cbz");
        _repo.Setup(r => r.GetByIdWithLibraryAsync(item.Id)).ReturnsAsync(item);
        _security.Setup(s => s.ValidateMediaAccess(item)).Returns(MediaAccessResult.Unauthorized);

        var result = await NewController().GetPage(item.Id, 1, CancellationToken.None);

        Assert.IsType<ForbidResult>(result);
    }
}
