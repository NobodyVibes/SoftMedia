using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SoftMedia.Server.Controllers;
using SoftMedia.Server.Data;
using SoftMedia.Server.Models;
using SoftMedia.Server.Services.Abstractions;
using SoftMedia.Server.Services.Media;
using SoftMedia.Server.Services.Security.LibraryAccess;
using Xunit;

namespace SoftMedia.Server.Tests.Controllers;

public class PhotosControllerTests : IDisposable
{
    private readonly Mock<IMediaRepository> _repo = new();
    private readonly Mock<IStreamSecurityService> _security = new();
    private readonly Mock<IThumbnailService> _thumbs = new();
    private readonly Mock<IUserLibraryAccessProvider> _access = new();
    private readonly AppDbContext _db = new(new DbContextOptionsBuilder<AppDbContext>()
        .UseInMemoryDatabase($"photos-ctrl-{Guid.NewGuid()}").Options);
    private readonly string _tempDir;
    private readonly string _photoPath;

    public PhotosControllerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "softmedia-photosctrl-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _photoPath = Path.Combine(_tempDir, "beach.jpg");
        File.WriteAllBytes(_photoPath, new byte[] { 0xFF, 0xD8, 0xFF, 0xD9 }); // minimal JPEG shell
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
    }

    private PhotosController BuildController()
    {
        _access.Setup(a => a.GetCurrentAsync()).ReturnsAsync(LibraryAccess.Unrestricted);
        return new PhotosController(
            _repo.Object, _security.Object, _thumbs.Object, _db, _access.Object,
            NullLogger<PhotosController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
    }

    private MediaItem SeedPhoto(MediaType type = MediaType.Photo)
    {
        var library = new Library { Id = Guid.NewGuid(), Name = "Photos", Type = LibraryType.Photo, Paths = new List<string> { _tempDir } };
        var item = new MediaItem
        {
            Id = Guid.NewGuid(),
            LibraryId = library.Id,
            Library = library,
            Type = type,
            Title = "beach",
            Path = _photoPath,
        };
        _repo.Setup(r => r.GetByIdWithLibraryAsync(item.Id)).ReturnsAsync(item);
        return item;
    }

    [Fact]
    public async Task GetImage_UnknownOrAclDeniedItem_Returns404()
    {
        // The ACL-gated repository resolves denied/unknown items to null (anti-probe).
        _repo.Setup(r => r.GetByIdWithLibraryAsync(It.IsAny<Guid>())).ReturnsAsync((MediaItem?)null);

        var result = await BuildController().GetImage(Guid.NewGuid(), width: null);

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task GetImage_NonPhotoItem_Returns404()
    {
        // A movie id must not be servable through the photo route.
        var item = SeedPhoto(MediaType.Movie);

        var result = await BuildController().GetImage(item.Id, width: null);

        Assert.IsType<NotFoundResult>(result);
        _security.Verify(s => s.ValidateMediaAccessAsync(It.IsAny<MediaItem>()), Times.Never);
    }

    [Fact]
    public async Task GetImage_JailOrAclRejection_Returns404()
    {
        var item = SeedPhoto();
        _security.Setup(s => s.ValidateMediaAccessAsync(item)).ReturnsAsync(MediaAccessResult.Unauthorized);

        var result = await BuildController().GetImage(item.Id, width: null);

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task GetImage_Allowed_NoWidth_ServesOriginalWithImageMime()
    {
        var item = SeedPhoto();
        _security.Setup(s => s.ValidateMediaAccessAsync(item)).ReturnsAsync(MediaAccessResult.Allowed);

        var result = await BuildController().GetImage(item.Id, width: null);

        var file = Assert.IsType<PhysicalFileResult>(result);
        Assert.Equal(_photoPath, file.FileName);
        Assert.Equal("image/jpeg", file.ContentType);
        Assert.True(file.EnableRangeProcessing);
        _thumbs.Verify(t => t.GetOrCreateThumbnailAsync(It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<int>()), Times.Never);
    }

    [Fact]
    public async Task GetImage_WidthInRange_ServesWebpThumbnail()
    {
        var item = SeedPhoto();
        _security.Setup(s => s.ValidateMediaAccessAsync(item)).ReturnsAsync(MediaAccessResult.Allowed);
        var thumbPath = Path.Combine(_tempDir, "thumb.webp");
        File.WriteAllBytes(thumbPath, new byte[] { 0x52 });
        _thumbs.Setup(t => t.GetOrCreateThumbnailAsync(_photoPath, item.Id, 480)).ReturnsAsync(thumbPath);

        var result = await BuildController().GetImage(item.Id, width: 480);

        var file = Assert.IsType<PhysicalFileResult>(result);
        Assert.Equal(thumbPath, file.FileName);
        Assert.Equal("image/webp", file.ContentType);
    }

    [Fact]
    public async Task GetImage_ThumbnailFailure_FallsBackToOriginal()
    {
        // HEIC-style case: no codec -> thumbnail null -> serve the original bytes.
        var item = SeedPhoto();
        _security.Setup(s => s.ValidateMediaAccessAsync(item)).ReturnsAsync(MediaAccessResult.Allowed);
        _thumbs.Setup(t => t.GetOrCreateThumbnailAsync(_photoPath, item.Id, 480)).ReturnsAsync((string?)null);

        var result = await BuildController().GetImage(item.Id, width: 480);

        var file = Assert.IsType<PhysicalFileResult>(result);
        Assert.Equal(_photoPath, file.FileName);
    }

    [Fact]
    public async Task GetImage_WidthOutOfRange_IgnoresThumbnailing()
    {
        var item = SeedPhoto();
        _security.Setup(s => s.ValidateMediaAccessAsync(item)).ReturnsAsync(MediaAccessResult.Allowed);

        var result = await BuildController().GetImage(item.Id, width: 5000);

        var file = Assert.IsType<PhysicalFileResult>(result);
        Assert.Equal(_photoPath, file.FileName);
        _thumbs.Verify(t => t.GetOrCreateThumbnailAsync(It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<int>()), Times.Never);
    }
}
