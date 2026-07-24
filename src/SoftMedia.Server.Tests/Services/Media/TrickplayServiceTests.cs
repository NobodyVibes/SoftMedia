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
    private readonly Mock<IBinaryLocationService> _binary = new();
    private readonly Mock<ISettingsService> _settings = new();

    public TrickplayServiceTests()
    {
        _webRoot = Path.Combine(Path.GetTempPath(), "sm-trickplay-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_webRoot);

        var env = new Mock<IWebHostEnvironment>();
        env.SetupGet(e => e.WebRootPath).Returns(_webRoot);

        _settings.Setup(s => s.GetSettingAsync("TrickplayIntervalSeconds", It.IsAny<int>())).ReturnsAsync(10);
        _settings.Setup(s => s.GetSettingAsync("TrickplayThumbnailWidth", It.IsAny<int>())).ReturnsAsync(320);
        _svc = new TrickplayService(env.Object, _binary.Object, _settings.Object, NullLogger<TrickplayService>.Instance);
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

    // SR-WI-028(a): manifest sheet order must be numeric on the sheet index —
    // ordinal string sorting put "sheet-10.jpg" before "sheet-2.jpg", scrambling
    // scrub previews past ~2h47m at the default cadence. Existing on-disk names
    // (unpadded sheet-N.jpg) must sort correctly without renames.
    [Fact]
    public void SortSheets_OrdersNumerically_NotOrdinally()
    {
        var shuffled = new[]
        {
            "sheet-10.jpg", "sheet-0.jpg", "sheet-2.jpg", "sheet-11.jpg", "sheet-1.jpg", "sheet-9.jpg",
        };

        var sorted = TrickplayService.SortSheets(shuffled);

        Assert.Equal(
            new[] { "sheet-0.jpg", "sheet-1.jpg", "sheet-2.jpg", "sheet-9.jpg", "sheet-10.jpg", "sheet-11.jpg" },
            sorted);
    }

    [Fact]
    public void SortSheets_UnparseableNamesSortLast()
    {
        var sorted = TrickplayService.SortSheets(new[] { "sheet-x.jpg", "sheet-3.jpg", "sheet-0.jpg" });
        Assert.Equal(new[] { "sheet-0.jpg", "sheet-3.jpg", "sheet-x.jpg" }, sorted);
    }

    // ---- SR-WI-028(b): generation must kill the FFmpeg child on cancellation and
    // enforce a run-time ceiling. A fake looping "ffmpeg" (a .cmd that appends to a
    // heartbeat file every ~1s) stands in for a stuck source; after GenerateAsync
    // returns, the heartbeat must stop growing — proof the process tree was killed.

    private (string sourcePath, string alivePath) SeedFakeFfmpeg()
    {
        var sourcePath = Path.Combine(_webRoot, "source.mp4");
        File.WriteAllBytes(sourcePath, new byte[] { 0, 0, 0, 0 });

        var alivePath = Path.Combine(_webRoot, "alive.txt");
        var scriptPath = Path.Combine(_webRoot, "fake-ffmpeg.cmd");
        File.WriteAllText(scriptPath,
            "@echo off\r\n" +
            ":loop\r\n" +
            $"echo alive >> \"{alivePath}\"\r\n" +
            "ping -n 2 127.0.0.1 > nul\r\n" +
            "goto loop\r\n");
        _binary.Setup(b => b.ResolveFFmpegPath()).Returns(scriptPath);
        return (sourcePath, alivePath);
    }

    private static long AliveLength(string alivePath) =>
        File.Exists(alivePath) ? new FileInfo(alivePath).Length : 0;

    private static async Task AssertHeartbeatStopsAsync(string alivePath)
    {
        // Give a straggler one loop iteration to flush, then require stability.
        await Task.Delay(1500);
        var len = AliveLength(alivePath);
        await Task.Delay(2500);
        Assert.Equal(len, AliveLength(alivePath));
    }

    [Fact]
    public async Task GenerateAsync_Cancellation_KillsFfmpegProcessTree()
    {
        if (!OperatingSystem.IsWindows()) return; // fake ffmpeg is a Windows .cmd

        var (sourcePath, alivePath) = SeedFakeFfmpeg();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(700));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => _svc.GenerateAsync(Guid.NewGuid(), sourcePath, cts.Token));

        Assert.True(AliveLength(alivePath) > 0, "fake ffmpeg never started");
        await AssertHeartbeatStopsAsync(alivePath);
    }

    [Fact]
    public async Task GenerateAsync_Timeout_ReturnsFalse_AndKillsFfmpegProcessTree()
    {
        if (!OperatingSystem.IsWindows()) return; // fake ffmpeg is a Windows .cmd

        var (sourcePath, alivePath) = SeedFakeFfmpeg();
        _svc.GenerationTimeout = TimeSpan.FromMilliseconds(700);

        var ok = await _svc.GenerateAsync(Guid.NewGuid(), sourcePath, CancellationToken.None);

        Assert.False(ok);
        Assert.True(AliveLength(alivePath) > 0, "fake ffmpeg never started");
        await AssertHeartbeatStopsAsync(alivePath);
    }
}
