using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SoftMedia.Server.Services.Background;
using SoftMedia.Server.Services.Transcoding;
using SoftMedia.Server.Services.Transcoding.Models;
using Xunit;

namespace SoftMedia.Server.Tests.Services.Background;

/// Phase 4 / C1 — verifies the segment janitor removes ONLY stale session
/// directories that the session manager has already discarded, and never
/// touches anything outside the configured temp root.
public class TranscodeSegmentCleanupServiceTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly Mock<ITranscodeService> _transcodeService;
    private readonly Mock<ITranscodeSessionManager> _sessionManager;

    public TranscodeSegmentCleanupServiceTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "transcode-cleanup-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);

        _transcodeService = new Mock<ITranscodeService>();
        _transcodeService.Setup(s => s.GetTempDir()).Returns(_tempRoot);

        _sessionManager = new Mock<ITranscodeSessionManager>();
        _sessionManager.Setup(s => s.GetAllSessions()).Returns(Array.Empty<TranscodeSession>());
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
            .BuildServiceProvider();
        return new TranscodeSegmentCleanupService(sp, NullLogger<TranscodeSegmentCleanupService>.Instance);
    }

    private string CreateSessionDir(string name, TimeSpan age)
    {
        var dir = Path.Combine(_tempRoot, name);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "seg_001.ts"), "fake segment");
        Directory.SetLastWriteTimeUtc(dir, DateTime.UtcNow - age);
        return dir;
    }

    [Fact]
    public void RunOnce_DirOlderThanThreshold_AndNotInOpenSet_IsDeleted()
    {
        var stale = CreateSessionDir("session-stale", TimeSpan.FromMinutes(30));

        NewService().RunOnce();

        Assert.False(Directory.Exists(stale));
    }

    [Fact]
    public void RunOnce_DirYoungerThanThreshold_IsRetained()
    {
        // 2 minutes old, threshold is 10 — must NOT be deleted yet.
        var fresh = CreateSessionDir("session-fresh", TimeSpan.FromMinutes(2));

        NewService().RunOnce();

        Assert.True(Directory.Exists(fresh));
    }

    [Fact]
    public void RunOnce_OpenSession_IsRetainedRegardlessOfAge()
    {
        // Even though this directory is much older than threshold, it must be
        // kept because the session manager still considers it active.
        var open = CreateSessionDir("session-open", TimeSpan.FromHours(2));

        _sessionManager.Setup(s => s.GetAllSessions()).Returns(new[]
        {
            new TranscodeSession
            {
                Key = new TranscodeSessionKey(Guid.NewGuid(), Guid.NewGuid(), null, null),
                SessionDirectory = open,
            }
        });

        NewService().RunOnce();

        Assert.True(Directory.Exists(open));
    }

    [Fact]
    public void RunOnce_TempRootMissing_DoesNotThrow()
    {
        Directory.Delete(_tempRoot, recursive: true);
        // Should be a silent no-op; the service is robust to a missing root.
        NewService().RunOnce();
        // No assertion needed — the absence of an exception IS the assertion.
    }

    [Fact]
    public void RunOnce_NeverDeletesFilesOutsideTempRoot()
    {
        // Create a sibling directory at the same level as _tempRoot. The janitor
        // walks Directory.EnumerateDirectories(_tempRoot) so this should be
        // physically unreachable, but we assert it anyway as a safety net.
        var siblingRoot = Path.Combine(Path.GetTempPath(), "transcode-cleanup-tests-sibling-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(siblingRoot);
        try
        {
            File.WriteAllText(Path.Combine(siblingRoot, "important.txt"), "do not delete");
            Directory.SetLastWriteTimeUtc(siblingRoot, DateTime.UtcNow - TimeSpan.FromHours(2));

            // Plant a stale child INSIDE the temp root so the loop has something to do.
            CreateSessionDir("inside-stale", TimeSpan.FromMinutes(30));

            NewService().RunOnce();

            Assert.True(Directory.Exists(siblingRoot));
            Assert.True(File.Exists(Path.Combine(siblingRoot, "important.txt")));
        }
        finally
        {
            try { Directory.Delete(siblingRoot, recursive: true); } catch { }
        }
    }
}
