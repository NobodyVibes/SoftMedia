using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using SoftMedia.Server.Data;
using SoftMedia.Server.Models;
using SoftMedia.Server.Services.Abstractions;
using SoftMedia.Server.Services.Infrastructure;
using SoftMedia.Server.Services.Media.Detection;
using SoftMedia.Server.Services.Scanning; // For LibraryScanQueueService and IScannerOrchestrator
using Xunit;

namespace SoftMedia.Server.Tests.Services.Scanning;

public class LibraryScanQueueServiceTests
{
    private readonly Mock<IServiceScopeFactory> _mockScopeFactory;
    private readonly Mock<IServiceScope> _mockScope;
    private readonly Mock<IServiceProvider> _mockServiceProvider;
    private readonly Mock<ILogger<LibraryScanQueueService>> _mockLogger;
    private readonly Mock<IScannerOrchestrator> _mockOrchestrator;
    private readonly Mock<ILibraryService> _mockLibraryService;

    public LibraryScanQueueServiceTests()
    {
        _mockScopeFactory = new Mock<IServiceScopeFactory>();
        _mockScope = new Mock<IServiceScope>();
        _mockServiceProvider = new Mock<IServiceProvider>();

        _mockScopeFactory.Setup(x => x.CreateScope()).Returns(_mockScope.Object);
        _mockScope.Setup(x => x.ServiceProvider).Returns(_mockServiceProvider.Object);

        _mockOrchestrator = new Mock<IScannerOrchestrator>();
        _mockLibraryService = new Mock<ILibraryService>();

        _mockServiceProvider.Setup(x => x.GetService(typeof(IScannerOrchestrator))).Returns(_mockOrchestrator.Object);
        _mockServiceProvider.Setup(x => x.GetService(typeof(ILibraryService))).Returns(_mockLibraryService.Object);
        
        // Mock required IServiceScopeFactory inside the scope if needed?
        // No, LibraryScanQueueService uses the injected one.

        _mockLogger = new Mock<ILogger<LibraryScanQueueService>>();
    }

    private LibraryScanQueueService CreateService(
        SoftMedia.Server.Services.Metadata.IMetadataQueue? metadataQueue = null,
        IImageDownloadQueue? imageQueue = null)
    {
        // Real in-memory dispatcher; tests don't assert webhook delivery here.
        return new LibraryScanQueueService(
            _mockScopeFactory.Object,
            _mockLogger.Object,
            new SoftMedia.Server.Services.Infrastructure.WebhookDispatcher(),
            registry: null,
            notifications: null,
            metadataQueue: metadataQueue,
            imageQueue: imageQueue);
    }

    [Fact]
    public async Task EnqueueScan_RunsScanViaOrchestrator()
    {
        // Arrange
        var service = CreateService();
        var libId = Guid.NewGuid();
        
        using var cts = new CancellationTokenSource();
        var task = service.StartAsync(cts.Token);

        // Act
        service.EnqueueScan(libId, "Test Library");

        // Assert
        // Wait for execution
        int retries = 0;
        bool called = false;
        while (retries < 20)
        {
            try
            {
                _mockOrchestrator.Verify(x => x.ExecuteScanAsync(libId, It.IsAny<IProgress<ScanProgress>>(), It.IsAny<CancellationToken>()), Times.Once);
                called = true;
                break;
            }
            catch (MockException) 
            {
                await Task.Delay(50);
                retries++;
            }
        }

        cts.Cancel();
        
        Assert.True(called, "Orchestrator was not called.");
    }
    
    [Fact]
    public async Task EnqueueScan_SerializesConcurrentRequests()
    {
        // Arrange
        var service = CreateService();
        var libId1 = Guid.NewGuid();
        var libId2 = Guid.NewGuid();
        
        // Setup Orchestrator to block on first call
        var tcs1 = new TaskCompletionSource<bool>();
        _mockOrchestrator.Setup(x => x.ExecuteScanAsync(libId1, It.IsAny<IProgress<ScanProgress>>(), It.IsAny<CancellationToken>()))
            .Returns(async () => {
                await tcs1.Task;
            });
            
        _mockOrchestrator.Setup(x => x.ExecuteScanAsync(libId2, It.IsAny<IProgress<ScanProgress>>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        using var cts = new CancellationTokenSource();
        var task = service.StartAsync(cts.Token);

        // Act
        service.EnqueueScan(libId1, "Lib 1");
        service.EnqueueScan(libId2, "Lib 2");

        // Assert
        // 1. Wait for Lib 1 to start
         int retries = 0;
        while (retries < 20)
        {
            try
            {
                _mockOrchestrator.Verify(x => x.ExecuteScanAsync(libId1, It.IsAny<IProgress<ScanProgress>>(), It.IsAny<CancellationToken>()), Times.Once);
                break;
            }
            catch (MockException) 
            {
                await Task.Delay(50);
                retries++;
            }
        }
        
        // 2. Verify Lib 2 has NOT started yet
         _mockOrchestrator.Verify(x => x.ExecuteScanAsync(libId2, It.IsAny<IProgress<ScanProgress>>(), It.IsAny<CancellationToken>()), Times.Never);

        // 3. Release Lib 1
        tcs1.SetResult(true);

        // 4. Wait for Lib 2 to start
        retries = 0;
        bool lib2Called = false;
        while (retries < 20)
        {
            try
            {
                _mockOrchestrator.Verify(x => x.ExecuteScanAsync(libId2, It.IsAny<IProgress<ScanProgress>>(), It.IsAny<CancellationToken>()), Times.Once);
                lib2Called = true;
                break;
            }
            catch (MockException) 
            {
                await Task.Delay(50);
                retries++;
            }
        }
        
        cts.Cancel();

        Assert.True(lib2Called, "Second library scan was not processed after first completed.");
    }

    [Fact]
    public void EnqueueIntroCreditsDetection_DedupesByTargetSeriesId()
    {
        var service = CreateService();
        var seriesId = Guid.NewGuid();

        var first = service.EnqueueIntroCreditsDetection(seriesId, "Some Show");
        var second = service.EnqueueIntroCreditsDetection(seriesId, "Some Show");

        Assert.Equal(first.Id, second.Id);
        Assert.Equal(LibraryScanJobType.IntroCreditsDetection, first.Type);
        Assert.Equal(seriesId, first.TargetSeriesId);
    }

    [Fact]
    public async Task EnqueueIntroCreditsDetection_RunsDetectorViaScopedService()
    {
        var service = CreateService();
        var seriesId = Guid.NewGuid();

        var detector = new Mock<IIntroCreditsDetectionService>();
        detector
            .Setup(d => d.DetectAsync(seriesId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new IntroCreditsDetectionResult(EpisodesProcessed: 3, IntrosFound: 2, CreditsFound: 2, FailureReason: null));
        _mockServiceProvider
            .Setup(x => x.GetService(typeof(IIntroCreditsDetectionService)))
            .Returns(detector.Object);

        using var cts = new CancellationTokenSource();
        var task = service.StartAsync(cts.Token);

        service.EnqueueIntroCreditsDetection(seriesId, "Some Show");

        var called = await WaitForCalledAsync(() =>
            detector.Verify(d => d.DetectAsync(seriesId, It.IsAny<CancellationToken>()), Times.Once));

        cts.Cancel();
        Assert.True(called, "Detection service was not called.");
    }

    [Fact]
    public void EnqueueIntroCreditsDetection_CarriesTargetSeriesId_OnReturnedJob()
    {
        var service = CreateService();
        var seriesId = Guid.NewGuid();

        var job = service.EnqueueIntroCreditsDetection(seriesId, "Daring Do");

        Assert.Equal(seriesId, job.TargetSeriesId);
        Assert.Equal(LibraryScanJobType.IntroCreditsDetection, job.Type);
        Assert.Contains("Daring Do", job.LibraryName);
    }

    [Fact]
    public async Task EnqueueScan_PreemptsRunningDetection_WhichRequeuesAndFinishesLater()
    {
        var service = CreateService();
        var seriesId = Guid.NewGuid();
        var libId = Guid.NewGuid();

        // First detection call blocks until preempted (cancelled); the re-run succeeds.
        int detectCalls = 0;
        var firstCallStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var detector = new Mock<IIntroCreditsDetectionService>();
        detector
            .Setup(d => d.DetectAsync(seriesId, It.IsAny<CancellationToken>()))
            .Returns(async (Guid _, CancellationToken ct) =>
            {
                if (Interlocked.Increment(ref detectCalls) == 1)
                {
                    firstCallStarted.TrySetResult(true);
                    await Task.Delay(Timeout.Infinite, ct); // held until preemption cancels
                }
                return new IntroCreditsDetectionResult(EpisodesProcessed: 2, IntrosFound: 1, CreditsFound: 1, FailureReason: null);
            });
        _mockServiceProvider
            .Setup(x => x.GetService(typeof(IIntroCreditsDetectionService)))
            .Returns(detector.Object);
        _mockOrchestrator
            .Setup(x => x.ExecuteScanAsync(libId, It.IsAny<IProgress<ScanProgress>>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        using var cts = new CancellationTokenSource();
        await service.StartAsync(cts.Token);

        var detectionJob = service.EnqueueIntroCreditsDetection(seriesId, "Some Show");
        await firstCallStarted.Task.WaitAsync(TimeSpan.FromSeconds(5)); // detection is mid-run

        // A library scan arrives: it must preempt the detection and run promptly.
        var scanJob = service.EnqueueScan(libId, "Test Library");
        var scanCompleted = await WaitForConditionAsync(() =>
            service.GetJobStatus(scanJob.Id)?.Status == LibraryScanStatus.Completed, retries: 60);
        Assert.True(scanCompleted, "Scan did not complete promptly while detection was running.");

        // The preempted detection re-queues (not Failed) and completes on its re-run.
        var detectionCompleted = await WaitForConditionAsync(() =>
            service.GetJobStatus(detectionJob.Id)?.Status == LibraryScanStatus.Completed, retries: 60);
        cts.Cancel();

        Assert.True(detectionCompleted, "Preempted detection was not re-run to completion.");
        Assert.True(detectCalls >= 2, "Detection was not re-invoked after preemption.");
    }

    [Fact]
    public async Task DetectionSummary_ReportsPaused_WhilePrimaryJobsHaveTheQueue()
    {
        var service = CreateService();
        var libId = Guid.NewGuid();
        var seriesId = Guid.NewGuid();

        var scanGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _mockOrchestrator
            .Setup(x => x.ExecuteScanAsync(libId, It.IsAny<IProgress<ScanProgress>>(), It.IsAny<CancellationToken>()))
            .Returns(async () => { await scanGate.Task; });

        var detector = new Mock<IIntroCreditsDetectionService>();
        detector
            .Setup(d => d.DetectAsync(seriesId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new IntroCreditsDetectionResult(EpisodesProcessed: 2, IntrosFound: 1, CreditsFound: 1, FailureReason: null));
        _mockServiceProvider
            .Setup(x => x.GetService(typeof(IIntroCreditsDetectionService)))
            .Returns(detector.Object);

        using var cts = new CancellationTokenSource();
        await service.StartAsync(cts.Token);

        var scan = service.EnqueueScan(libId, "Test Library");
        var scanRunning = await WaitForConditionAsync(() =>
            service.GetJobStatus(scan.Id)?.Status == LibraryScanStatus.Running);
        Assert.True(scanRunning, "Scan did not start.");

        var detectionJob = service.EnqueueIntroCreditsDetection(seriesId, "Some Show");

        // While the scan holds the queue, the detection summary must read Paused —
        // not silently sit as an anonymous queued row.
        var summary = service.GetAllJobs().FirstOrDefault(j => j.Id == LibraryScanQueueService.DetectionSummaryJobId);
        Assert.NotNull(summary);
        Assert.Equal(LibraryScanStatus.Paused, summary!.Status);

        // Release the scan: detection resumes and completes on its own.
        scanGate.SetResult(true);
        var detectionDone = await WaitForConditionAsync(() =>
            service.GetJobStatus(detectionJob.Id)?.Status == LibraryScanStatus.Completed, retries: 60);
        cts.Cancel();
        Assert.True(detectionDone, "Detection did not resume after the scan finished.");
    }

    [Fact]
    public async Task Detection_DoesNotRun_WhileScanHoldsMetadataStage()
    {
        var libId = Guid.NewGuid();
        var seriesId = Guid.NewGuid();
        var pending = 2;
        var metadataQueue = new Mock<SoftMedia.Server.Services.Metadata.IMetadataQueue>();
        metadataQueue.Setup(x => x.GetPendingCountForLibrary(libId)).Returns(() => pending);

        var service = CreateService(metadataQueue.Object);
        _mockOrchestrator
            .Setup(x => x.ExecuteScanAsync(libId, It.IsAny<IProgress<ScanProgress>>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        int detectorCalls = 0;
        var detector = new Mock<IIntroCreditsDetectionService>();
        detector
            .Setup(d => d.DetectAsync(seriesId, It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                Interlocked.Increment(ref detectorCalls);
                return Task.FromResult(new IntroCreditsDetectionResult(EpisodesProcessed: 2, IntrosFound: 1, CreditsFound: 1, FailureReason: null));
            });
        _mockServiceProvider
            .Setup(x => x.GetService(typeof(IIntroCreditsDetectionService)))
            .Returns(detector.Object);

        using var cts = new CancellationTokenSource();
        await service.StartAsync(cts.Token);

        // Scan's file walk finishes instantly, but enrichment is pending → the job
        // holds its Metadata stage (still Running from the user's perspective).
        var scan = service.EnqueueScan(libId, "Test Library");
        var inMetadata = await WaitForConditionAsync(() =>
            service.GetJobStatus(scan.Id)?.Stage == LibraryScanStage.Metadata &&
            service.GetJobStatus(scan.Id)?.Status == LibraryScanStatus.Running);
        Assert.True(inMetadata, "Scan did not enter Metadata stage.");

        var detectionJob = service.EnqueueIntroCreditsDetection(seriesId, "Some Show");

        // Several loop cycles: detection must NOT start while the scan is draining.
        await Task.Delay(1500);
        Assert.Equal(0, detectorCalls);
        Assert.Equal(LibraryScanStatus.Queued, service.GetJobStatus(detectionJob.Id)!.Status);

        // Drain completes → the monitor finalizes the scan → detection may now run.
        pending = 0;
        var detectionDone = await WaitForConditionAsync(() =>
            service.GetJobStatus(detectionJob.Id)?.Status == LibraryScanStatus.Completed, retries: 100);
        cts.Cancel();
        Assert.True(detectionDone, "Detection did not run after the scan fully completed.");
    }

    [Fact]
    public async Task Scan_MapsProgressStageAndErrors_OntoJob()
    {
        var service = CreateService();
        var libId = Guid.NewGuid();

        _mockOrchestrator
            .Setup(x => x.ExecuteScanAsync(libId, It.IsAny<IProgress<ScanProgress>>(), It.IsAny<CancellationToken>()))
            .Returns<Guid, IProgress<ScanProgress>?, CancellationToken>((_, progress, _) =>
            {
                progress!.Report(new ScanProgress(0, 42, null, "Discovering files...", Stage: LibraryScanStage.Discovery));
                progress.Report(new ScanProgress(10, 42, "file.mkv", "Scanning files...",
                    NewCount: 3, UpdatedCount: 2, SkippedCount: 4, ErrorCount: 1));
                return Task.CompletedTask;
            });

        using var cts = new CancellationTokenSource();
        await service.StartAsync(cts.Token);
        var job = service.EnqueueScan(libId, "Test Library");

        var completed = await WaitForConditionAsync(() =>
            service.GetJobStatus(job.Id)?.Status == LibraryScanStatus.Completed);
        cts.Cancel();

        Assert.True(completed, "Job did not complete.");
        var finished = service.GetJobStatus(job.Id)!;
        Assert.Equal(42, finished.TotalFiles);
        Assert.Equal(3, finished.NewItems);
        Assert.Equal(2, finished.UpdatedItems);
        Assert.Equal(4, finished.SkippedItems);
        Assert.Equal(1, finished.ErrorCount);
    }

    [Fact]
    public async Task Scan_WithPendingEnrichment_HoldsJobInMetadataStage_ThenFinalizesOnDrain()
    {
        var libId = Guid.NewGuid();
        var pending = 3;
        var metadataQueue = new Mock<SoftMedia.Server.Services.Metadata.IMetadataQueue>();
        metadataQueue.Setup(x => x.GetPendingCountForLibrary(libId)).Returns(() => pending);

        var service = CreateService(metadataQueue.Object);
        _mockOrchestrator
            .Setup(x => x.ExecuteScanAsync(libId, It.IsAny<IProgress<ScanProgress>>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        using var cts = new CancellationTokenSource();
        await service.StartAsync(cts.Token);
        var job = service.EnqueueScan(libId, "Test Library");

        // Catalog work finishes but enrichment is pending → job must stay Running in Metadata stage
        var enteredMetadata = await WaitForConditionAsync(() =>
        {
            var j = service.GetJobStatus(job.Id);
            return j?.Stage == LibraryScanStage.Metadata && j.Status == LibraryScanStatus.Running;
        });
        Assert.True(enteredMetadata, "Job did not enter Metadata stage.");
        Assert.Equal(3, service.GetJobStatus(job.Id)!.MetadataTotal);

        // Drain the gauge → the monitor must finalize the job
        pending = 0;
        var completed = await WaitForConditionAsync(() =>
            service.GetJobStatus(job.Id)?.Status == LibraryScanStatus.Completed, retries: 60);
        cts.Cancel();

        Assert.True(completed, "Job was not finalized after enrichment drained.");
        Assert.Equal(0, service.GetJobStatus(job.Id)!.MetadataRemaining);
    }

    [Fact]
    public async Task Scan_WithPendingImageDownloads_HoldsJobOpen_EvenWhenMetadataDrained()
    {
        var libId = Guid.NewGuid();
        var imagesPending = 2;

        // Metadata queue already drained; only artwork downloads remain.
        var metadataQueue = new Mock<SoftMedia.Server.Services.Metadata.IMetadataQueue>();
        metadataQueue.Setup(x => x.GetPendingCountForLibrary(libId)).Returns(0);
        var imageQueue = new Mock<IImageDownloadQueue>();
        imageQueue.Setup(x => x.GetPendingCountForLibrary(libId)).Returns(() => imagesPending);

        var service = CreateService(metadataQueue.Object, imageQueue.Object);
        _mockOrchestrator
            .Setup(x => x.ExecuteScanAsync(libId, It.IsAny<IProgress<ScanProgress>>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        using var cts = new CancellationTokenSource();
        await service.StartAsync(cts.Token);
        var job = service.EnqueueScan(libId, "Test Library");

        var enteredMetadata = await WaitForConditionAsync(() =>
        {
            var j = service.GetJobStatus(job.Id);
            return j?.Stage == LibraryScanStage.Metadata && j.Status == LibraryScanStatus.Running;
        });
        Assert.True(enteredMetadata, "Job did not stay open for pending image downloads.");

        imagesPending = 0;
        var completed = await WaitForConditionAsync(() =>
            service.GetJobStatus(job.Id)?.Status == LibraryScanStatus.Completed, retries: 60);
        cts.Cancel();

        Assert.True(completed, "Job was not finalized after image downloads drained.");
    }

    [Fact]
    public async Task Scan_WithNoPendingEnrichment_CompletesImmediately()
    {
        var libId = Guid.NewGuid();
        var metadataQueue = new Mock<SoftMedia.Server.Services.Metadata.IMetadataQueue>();
        metadataQueue.Setup(x => x.GetPendingCountForLibrary(libId)).Returns(0);

        var service = CreateService(metadataQueue.Object);
        _mockOrchestrator
            .Setup(x => x.ExecuteScanAsync(libId, It.IsAny<IProgress<ScanProgress>>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        using var cts = new CancellationTokenSource();
        await service.StartAsync(cts.Token);
        var job = service.EnqueueScan(libId, "Test Library");

        var completed = await WaitForConditionAsync(() =>
            service.GetJobStatus(job.Id)?.Status == LibraryScanStatus.Completed);
        cts.Cancel();

        Assert.True(completed, "Job did not complete immediately with an empty enrichment gauge.");
    }

    [Fact]
    public async Task Scan_EnqueuedAfterDetectionBacklog_RunsBeforeDetections()
    {
        var service = CreateService();
        var libId = Guid.NewGuid();
        var order = new System.Collections.Concurrent.ConcurrentQueue<string>();

        var detector = new Mock<IIntroCreditsDetectionService>();
        detector
            .Setup(d => d.DetectAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .Returns<Guid, CancellationToken>((_, _) =>
            {
                order.Enqueue("detection");
                return Task.FromResult(new IntroCreditsDetectionResult(1, 1, 1, null));
            });
        _mockServiceProvider
            .Setup(x => x.GetService(typeof(IIntroCreditsDetectionService)))
            .Returns(detector.Object);

        _mockOrchestrator
            .Setup(x => x.ExecuteScanAsync(libId, It.IsAny<IProgress<ScanProgress>>(), It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                order.Enqueue("scan");
                return Task.CompletedTask;
            });

        // Detection backlog first, scan LAST — with FIFO the scan would wait behind both.
        service.EnqueueIntroCreditsDetection(Guid.NewGuid(), "Show A");
        service.EnqueueIntroCreditsDetection(Guid.NewGuid(), "Show B");
        service.EnqueueScan(libId, "Movies");

        using var cts = new CancellationTokenSource();
        await service.StartAsync(cts.Token);

        var allRan = await WaitForConditionAsync(() => order.Count == 3, retries: 60);
        cts.Cancel();

        Assert.True(allRan, "Not all jobs were processed.");
        Assert.True(order.TryDequeue(out var first));
        Assert.Equal("scan", first); // scan preempts the detection backlog
    }

    [Fact]
    public void GetAllJobs_CollapsesDetectionJobs_IntoSingleSummaryRow()
    {
        var service = CreateService();

        var jobA = service.EnqueueIntroCreditsDetection(Guid.NewGuid(), "Show A");
        var jobB = service.EnqueueIntroCreditsDetection(Guid.NewGuid(), "Show B");
        var jobC = service.EnqueueIntroCreditsDetection(Guid.NewGuid(), "Show C");

        var detectionRows = service.GetAllJobs()
            .Where(j => j.Type == LibraryScanJobType.IntroCreditsDetection)
            .ToList();

        var summary = Assert.Single(detectionRows);
        Assert.Equal(LibraryScanQueueService.DetectionSummaryJobId, summary.Id);
        Assert.Equal(3, summary.TotalFiles);
        Assert.Equal(0, summary.ProcessedFiles);
        Assert.Equal(LibraryScanStatus.Queued, summary.Status);

        // Individual jobs stay addressable for per-series status polling
        Assert.NotNull(service.GetJobStatus(jobA.Id));
        Assert.NotNull(service.GetJobStatus(jobB.Id));
        Assert.NotNull(service.GetJobStatus(jobC.Id));
    }

    [Fact]
    public async Task DetectionSummary_TracksBatchProgress_ToCompletion()
    {
        var service = CreateService();
        var detector = new Mock<IIntroCreditsDetectionService>();
        detector
            .Setup(d => d.DetectAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new IntroCreditsDetectionResult(EpisodesProcessed: 3, IntrosFound: 2, CreditsFound: 2, FailureReason: null));
        _mockServiceProvider
            .Setup(x => x.GetService(typeof(IIntroCreditsDetectionService)))
            .Returns(detector.Object);

        using var cts = new CancellationTokenSource();
        await service.StartAsync(cts.Token);

        service.EnqueueIntroCreditsDetection(Guid.NewGuid(), "Show A");
        service.EnqueueIntroCreditsDetection(Guid.NewGuid(), "Show B");

        var completed = await WaitForConditionAsync(() =>
        {
            var summary = service.GetAllJobs().FirstOrDefault(j => j.Type == LibraryScanJobType.IntroCreditsDetection);
            return summary is { Status: LibraryScanStatus.Completed, ProcessedFiles: 2, TotalFiles: 2 };
        }, retries: 60);
        cts.Cancel();

        Assert.True(completed, "Detection summary did not reach completed 2/2 state.");
        var final = service.GetAllJobs().First(j => j.Type == LibraryScanJobType.IntroCreditsDetection);
        Assert.Equal(8, final.UpdatedItems); // (2 intros + 2 credits) × 2 shows
        Assert.Equal(0, final.ErrorCount);
    }

    private static async Task<bool> WaitForCalledAsync(Action verify, int retries = 20, int delayMs = 50)
    {
        for (int i = 0; i < retries; i++)
        {
            try { verify(); return true; }
            catch (MockException) { await Task.Delay(delayMs); }
        }
        return false;
    }

    private static async Task<bool> WaitForConditionAsync(Func<bool> condition, int retries = 40, int delayMs = 50)
    {
        for (int i = 0; i < retries; i++)
        {
            if (condition()) return true;
            await Task.Delay(delayMs);
        }
        return false;
    }
}
