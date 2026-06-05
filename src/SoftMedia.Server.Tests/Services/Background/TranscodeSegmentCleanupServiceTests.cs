using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SoftMedia.Server.Services.Background;
using SoftMedia.Server.Services.Infrastructure;
using SoftMedia.Server.Services.Transcoding;
using SoftMedia.Server.Services.Transcoding.Models;
using Xunit;

namespace SoftMedia.Server.Tests.Services.Background;

/// Verifies the hourly transcode janitor: session folders are RETAINED for the
/// configured retention window (so playback can resume) and pruned once their newest
/// segment ages past it; live (transcoding/throttled) sessions are never touched, and
/// nothing outside the temp root is removed.
public class TranscodeSegmentCleanupServiceTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly Mock<ITranscodeService> _transcodeService;
    private readonly Mock<ITranscodeSessionManager> _sessionManager;
    private readonly Mock<ISettingsService> _settings;
    private int _retentionHours = 1; // tests use a 1h window unless overridden

    public TranscodeSegmentCleanupServiceTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "transcode-cleanup-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);

        _transcodeService = new Mock<ITranscodeService>();
        _transcodeService.Setup(s => s.GetTempDir()).Returns(_tempRoot);

        _sessionManager = new Mock<ITranscodeSessionManager>();
        _sessionManager.Setup(s => s.GetAllSessions()).Returns(Array.Empty<TranscodeSession>());

        _settings = new Mock<ISettingsService>();
        _settings.Setup(s => s.GetSettingAsync<int>("SegmentRetentionHours", It.IsAny<int>()))
            .ReturnsAsync(() => _retentionHours);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempRoot, recursive: true); } catch { /* best-effort */ }
    }

    private TranscodeSegmentCleanupService NewService()
    {
        var sp = new ServiceCollection()
            .AddSingleton(_transcodeService.Object)
            .AddSingleton(_sessionManager.Object)
            .AddSingleton(_settings.Object)
            .BuildServiceProvider();
        return new TranscodeSegmentCleanupService(sp, NullLogger<TranscodeSegmentCleanupService>.Instance);
    }

    private string CreateSessionDir(string name, TimeSpan age)
    {
        var dir = Path.Combine(_tempRoot, name);
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "seg_001.ts");
        File.WriteAllText(file, "fake segment");
        var when = DateTime.UtcNow - age;
        File.SetLastWriteTimeUtc(file, when);
        Directory.SetLastWriteTimeUtc(dir, when);
        return dir;
    }

    [Fact]
    public async Task NewestSegmentOlderThanRetention_IsDeleted()
    {
        var stale = CreateSessionDir("session-stale", TimeSpan.FromHours(2)); // retention is 1h

        await NewService().RunOnceAsync();

        Assert.False(Directory.Exists(stale));
    }

    [Fact]
    public async Task NewestSegmentWithinRetention_IsRetained()
    {
        var fresh = CreateSessionDir("session-fresh", TimeSpan.FromMinutes(2)); // within 1h

        await NewService().RunOnceAsync();

        Assert.True(Directory.Exists(fresh));
    }

    [Fact]
    public async Task LiveSession_IsRetainedRegardlessOfAge()
    {
        // A currently-transcoding session's folder must be kept even if its files look old.
        var open = CreateSessionDir("session-live", TimeSpan.FromHours(5));
        _sessionManager.Setup(s => s.GetAllSessions()).Returns(new[]
        {
            new TranscodeSession
            {
                Key = new TranscodeSessionKey(Guid.NewGuid(), Guid.NewGuid(), null, null),
                SessionDirectory = open,
                State = TranscodeState.Transcoding,
            }
        });

        await NewService().RunOnceAsync();

        Assert.True(Directory.Exists(open));
    }

    [Fact]
    public async Task DormantSessionPastRetention_IsDeleted_AndEvicted()
    {
        var dir = CreateSessionDir("session-dormant", TimeSpan.FromHours(2));
        var key = new TranscodeSessionKey(Guid.NewGuid(), Guid.NewGuid(), null, null);
        _sessionManager.Setup(s => s.GetAllSessions()).Returns(new[]
        {
            new TranscodeSession { Key = key, SessionDirectory = dir, State = TranscodeState.Dormant }
        });

        await NewService().RunOnceAsync();

        Assert.False(Directory.Exists(dir));
        _sessionManager.Verify(s => s.TryRemoveSession(key, out It.Ref<TranscodeSession?>.IsAny), Times.Once);
    }

    [Fact]
    public async Task ZeroRetention_DeletesNonLiveFoldersImmediately()
    {
        _retentionHours = 0;
        var dir = CreateSessionDir("session-recent", TimeSpan.FromSeconds(5)); // not live, no session tracked

        await NewService().RunOnceAsync();

        Assert.False(Directory.Exists(dir));
    }

    [Fact]
    public async Task TempRootMissing_DoesNotThrow()
    {
        Directory.Delete(_tempRoot, recursive: true);
        await NewService().RunOnceAsync(); // no-op, no exception
    }

    [Fact]
    public async Task NeverDeletesFilesOutsideTempRoot()
    {
        var siblingRoot = Path.Combine(Path.GetTempPath(), "transcode-cleanup-tests-sibling-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(siblingRoot);
        try
        {
            File.WriteAllText(Path.Combine(siblingRoot, "important.txt"), "do not delete");
            Directory.SetLastWriteTimeUtc(siblingRoot, DateTime.UtcNow - TimeSpan.FromHours(5));

            CreateSessionDir("inside-stale", TimeSpan.FromHours(2));

            await NewService().RunOnceAsync();

            Assert.True(Directory.Exists(siblingRoot));
            Assert.True(File.Exists(Path.Combine(siblingRoot, "important.txt")));
        }
        finally
        {
            try { Directory.Delete(siblingRoot, recursive: true); } catch { }
        }
    }
}
