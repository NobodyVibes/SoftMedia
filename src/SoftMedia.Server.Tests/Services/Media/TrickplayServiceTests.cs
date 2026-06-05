using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SoftMedia.Server.Services.Infrastructure;
using SoftMedia.Server.Services.Media;
using Xunit;

namespace SoftMedia.Server.Tests.Services.Media;

/// Covers the deterministic, FFmpeg-free parts of TrickplayService: cache layout,
/// HasTrickplay detection, manifest/sheet resolution, and the path-traversal guard.
/// (Sprite generation itself shells out to FFmpeg and is exercised manually.)
public class TrickplayServiceTests : IDisposable
{
    private readonly string _webRoot;
    private readonly TrickplayService _svc;

    public TrickplayServiceTests()
    {
        _webRoot = Path.Combine(Path.GetTempPath(), "sm-trickplay-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_webRoot);

        var env = new Mock<IWebHostEnvironment>();
        env.SetupGet(e => e.WebRootPath).Returns(_webRoot);

        var binary = new Mock<IBinaryLocationService>();
        var settings = new Mock<ISettingsService>();
        _svc = new TrickplayService(env.Object, binary.Object, settings.Object, NullLogger<TrickplayService>.Instance);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_webRoot)) Directory.Delete(_webRoot, recursive: true); } catch { }
    }

    private string SeedItem(Guid id, params string[] sheetFiles)
    {
        var dir = Path.Combine(_webRoot, "cache", "trickplay", id.ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "manifest.json"), "{\"version\":1}");
        foreach (var s in sheetFiles) File.WriteAllBytes(Path.Combine(dir, s), new byte[] { 0xFF, 0xD8, 0xFF });
        return dir;
    }

    [Fact]
    public void HasTrickplay_FalseWhenAbsent_TrueAfterSeed()
    {
        var id = Guid.NewGuid();
        Assert.False(_svc.HasTrickplay(id));
        SeedItem(id, "sheet-0.jpg");
        Assert.True(_svc.HasTrickplay(id));
    }

    [Fact]
    public void GetManifestPath_ReturnsPath_WhenPresent()
    {
        var id = Guid.NewGuid();
        Assert.Null(_svc.GetManifestPath(id));
        SeedItem(id);
        Assert.NotNull(_svc.GetManifestPath(id));
    }

    [Fact]
    public void GetSheetPath_ResolvesRealSheet()
    {
        var id = Guid.NewGuid();
        SeedItem(id, "sheet-0.jpg");
        Assert.NotNull(_svc.GetSheetPath(id, "sheet-0.jpg"));
    }

    [Theory]
    [InlineData("../../../etc/passwd")]
    [InlineData("..\\..\\windows\\system32")]
    [InlineData("sub/dir.jpg")]
    [InlineData("a/../../escape.jpg")]
    public void GetSheetPath_RejectsTraversal(string evil)
    {
        var id = Guid.NewGuid();
        SeedItem(id, "sheet-0.jpg");
        Assert.Null(_svc.GetSheetPath(id, evil));
    }

    [Fact]
    public void GetSheetPath_NullForMissingFile()
    {
        var id = Guid.NewGuid();
        SeedItem(id, "sheet-0.jpg");
        Assert.Null(_svc.GetSheetPath(id, "sheet-9.jpg"));
    }
}
