using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SoftMedia.Server.Models;
using SoftMedia.Server.Services.Abstractions;
using SoftMedia.Server.Services.Scanning;
using Xunit;

namespace SoftMedia.Server.Tests.Services.Scanning;

/// <summary>
/// SM-WI-013 — the watcher's single-file import must yield to the scan queue: a queued
/// or actively-walking scan for the same library would race the import (duplicate-row
/// mint, unlocked concurrent writes). A scan draining its Metadata stage has finished
/// walking and must NOT block imports (it can drain for hours).
/// </summary>
public class LibraryWatcherScanDeferTests
{
    private static LibraryScanJob Job(
        Guid libraryId,
        LibraryScanStatus status,
        LibraryScanStage stage = LibraryScanStage.Discovery,
        LibraryScanJobType type = LibraryScanJobType.LibraryScan)
        => new() { LibraryId = libraryId, Status = status, Stage = stage, Type = type };

    [Fact]
    public void Defers_for_queued_and_walking_scans_only()
    {
        var libraryId = Guid.NewGuid();

        // Queued scan: its walk will ingest the file — defer.
        Assert.True(LibraryWatcher.ShouldDeferForActiveScan(
            new[] { Job(libraryId, LibraryScanStatus.Queued) }, libraryId));

        // Running, still walking: defer.
        Assert.True(LibraryWatcher.ShouldDeferForActiveScan(
            new[] { Job(libraryId, LibraryScanStatus.Running, LibraryScanStage.Processing) }, libraryId));

        // Running but drained into the Metadata stage: walk done — do NOT defer.
        Assert.False(LibraryWatcher.ShouldDeferForActiveScan(
            new[] { Job(libraryId, LibraryScanStatus.Running, LibraryScanStage.Metadata) }, libraryId));

        // Completed job, other library's scan, or a detection job: no deferral.
        Assert.False(LibraryWatcher.ShouldDeferForActiveScan(
            new[] { Job(libraryId, LibraryScanStatus.Completed) }, libraryId));
        Assert.False(LibraryWatcher.ShouldDeferForActiveScan(
            new[] { Job(Guid.NewGuid(), LibraryScanStatus.Running) }, libraryId));
        Assert.False(LibraryWatcher.ShouldDeferForActiveScan(
            new[] { Job(libraryId, LibraryScanStatus.Running, LibraryScanStage.Processing,
                LibraryScanJobType.IntroCreditsDetection) }, libraryId));
    }

    private sealed class ProbeWatcher : LibraryWatcher
    {
        public ProbeWatcher(IServiceScopeFactory scopeFactory)
            : base(scopeFactory, NullLogger<LibraryWatcher>.Instance)
        {
        }

        public Task Drive(string filePath, Guid libraryId) => ProcessStableFileAsync(filePath, libraryId);
    }

    private static (ProbeWatcher Watcher, Mock<IScannerOrchestrator> Orchestrator) CreateWatcher(
        params LibraryScanJob[] jobs)
    {
        var queue = new Mock<ILibraryScanQueueService>();
        queue.Setup(q => q.GetAllJobs()).Returns(jobs);

        var orchestrator = new Mock<IScannerOrchestrator>();
        orchestrator
            .Setup(o => o.ProcessSingleFileAsync(It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var services = new ServiceCollection();
        services.AddScoped(_ => queue.Object);
        services.AddScoped(_ => orchestrator.Object);
        var scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();

        return (new ProbeWatcher(scopeFactory), orchestrator);
    }

    [Fact]
    public async Task StableFile_RependsInsteadOfProcessing_WhileScanIsWalking()
    {
        var libraryId = Guid.NewGuid();
        var (watcher, orchestrator) = CreateWatcher(
            Job(libraryId, LibraryScanStatus.Running, LibraryScanStage.Processing));

        await watcher.Drive(@"C:\watched\small.soldiers.1998.1080p.bluray.x264-veto.mkv", libraryId);

        orchestrator.Verify(
            o => o.ProcessSingleFileAsync(It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()),
            Times.Never);
        Assert.Equal(1, watcher.PendingFileCount); // re-pended for retry after the scan
    }

    [Fact]
    public async Task StableFile_Processes_WhileScanOnlyDrainsMetadata()
    {
        var libraryId = Guid.NewGuid();
        var (watcher, orchestrator) = CreateWatcher(
            Job(libraryId, LibraryScanStatus.Running, LibraryScanStage.Metadata));

        await watcher.Drive(@"C:\watched\small.soldiers.1998.1080p.bluray.x264-veto.mkv", libraryId);

        orchestrator.Verify(
            o => o.ProcessSingleFileAsync(It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()),
            Times.Once);
        Assert.Equal(0, watcher.PendingFileCount);
    }
}
