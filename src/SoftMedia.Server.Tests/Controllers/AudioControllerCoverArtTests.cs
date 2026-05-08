using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SoftMedia.Server.Controllers;
using SoftMedia.Server.Data;
using SoftMedia.Server.Models;
using SoftMedia.Server.Services.Security;
using SoftMedia.Server.Services.Security.LibraryAccess;
using Xunit;

namespace SoftMedia.Server.Tests.Controllers;

/// Todo 07 — AudioController.GetCoverArt path jailing. Verifies that the
/// cached-path branch routes through StreamSecurityService and rejects
/// traversal payloads, and that the fallback embedded-tag branch only runs
/// when the audio file itself passes the library jail check.
public class AudioControllerCoverArtTests : IDisposable
{
    private readonly string _wwwroot;
    private readonly AppDbContext _db;

    public AudioControllerCoverArtTests()
    {
        _wwwroot = Path.Combine(Path.GetTempPath(), "softmedia-wwwroot-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_wwwroot);
        Directory.CreateDirectory(Path.Combine(_wwwroot, "covers"));

        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"audiocontroller-tests-{Guid.NewGuid()}")
            .Options);
    }

    public void Dispose()
    {
        _db.Dispose();
        try { Directory.Delete(_wwwroot, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public async Task GetCoverArt_WithDotDotTraversal_Returns404()
    {
        var item = await SeedMediaItem("../../../etc/passwd");

        var result = await BuildController().GetCoverArt(item.Id);
        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task GetCoverArt_WithBackslashTraversal_Returns404()
    {
        var item = await SeedMediaItem(@"..\..\..\Windows\win.ini");

        var result = await BuildController().GetCoverArt(item.Id);
        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task GetCoverArt_UnknownItemId_Returns404()
    {
        var result = await BuildController().GetCoverArt(Guid.NewGuid());
        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task GetCoverArt_ValidInsideWwwroot_Returns200AndCorrectMime()
    {
        var coverFile = Path.Combine(_wwwroot, "covers", "test.png");
        // Minimal PNG (8-byte signature + IHDR would be nicer but the controller
        // picks MIME by extension only, and reads the bytes as-is from disk).
        await File.WriteAllBytesAsync(coverFile, new byte[] { 0x89, 0x50, 0x4E, 0x47 });

        var item = await SeedMediaItem("/covers/test.png");

        var result = await BuildController().GetCoverArt(item.Id);
        var file = Assert.IsType<FileStreamResult>(result);
        Assert.Equal("image/png", file.ContentType);
    }

    private async Task<MediaItem> SeedMediaItem(string coverArtPath)
    {
        var library = new Library
        {
            Id = Guid.NewGuid(),
            Name = "Music",
            Type = LibraryType.Music,
            Paths = new List<string> { _wwwroot }
        };
        _db.Libraries.Add(library);

        var item = new MediaItem
        {
            Id = Guid.NewGuid(),
            LibraryId = library.Id,
            Library = library,
            Title = "Track",
            SortTitle = "Track",
            Path = Path.Combine(_wwwroot, "no-such-audio.flac"),
            CoverArtPath = coverArtPath,
            Type = MediaType.Audio,
        };
        _db.MediaItems.Add(item);
        await _db.SaveChangesAsync();
        return item;
    }

    private AudioController BuildController()
    {
        var env = new Mock<IWebHostEnvironment>();
        env.SetupGet(e => e.WebRootPath).Returns(_wwwroot);

        // Wave C — these tests don't exercise per-user ACL; an Unrestricted
        // provider keeps the focus on path-jail behaviour the suite was written for.
        var unrestrictedAccess = new Mock<IUserLibraryAccessProvider>();
        unrestrictedAccess
            .Setup(p => p.GetCurrentAsync())
            .ReturnsAsync(LibraryAccess.Unrestricted);

        var streamSecurity = new StreamSecurityService(
            unrestrictedAccess.Object,
            NullLogger<StreamSecurityService>.Instance);

        var controller = new AudioController(
            _db,
            streamSecurity,
            env.Object,
            NullLogger<AudioController>.Instance);

        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext()
        };
        return controller;
    }
}
