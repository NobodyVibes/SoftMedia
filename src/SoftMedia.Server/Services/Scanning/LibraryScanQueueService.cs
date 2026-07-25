using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using SoftMedia.Server.Data;
using SoftMedia.Server.Models;
using SoftMedia.Server.Services.Abstractions;
using SoftMedia.Server.Services.Infrastructure;
using SoftMedia.Server.Services.Media.Detection;
using SoftMedia.Server.Services.Metadata;

namespace SoftMedia.Server.Services.Scanning;

/// <summary>
/// Interface for the library scan queue service.
/// </summary>
public interface ILibraryScanQueueService
{
    /// <summary>
    /// Enqueue a new library scan job.
    /// </summary>
    LibraryScanJob EnqueueScan(Guid libraryId, string libraryName);
    
    /// <summary>
    /// Enqueue a global metadata refresh job.
    /// </summary>
    LibraryScanJob EnqueueMetadataRefresh();

    /// <summary>
    /// Enqueue an intro / credits detection job for a single series. Returns the
    /// existing job if one is already queued or running for the same series.
    /// </summary>
    LibraryScanJob EnqueueIntroCreditsDetection(Guid seriesId, string seriesName);
    
    /// <summary>
    /// Get the status of a specific scan job.
    /// </summary>
    LibraryScanJob? GetJobStatus(Guid jobId);
    
    /// <summary>
    /// Get all active and queued scan jobs.
    /// </summary>
    IEnumerable<LibraryScanJob> GetAllJobs();
    
    /// <summary>
    /// Check if a library already has a pending or running scan.
    /// </summary>
    bool IsLibraryInQueue(Guid libraryId);
    
    /// <summary>
    /// Update progress for a running scan job.
    /// </summary>
    void UpdateProgress(Guid jobId, LibraryScanStage stage, int processedFiles, int totalFiles, 
        string? currentFile = null, int newItems = 0, int updatedItems = 0, int skippedItems = 0);
    
    /// <summary>
    /// Mark a job as completed.
    /// </summary>
    void CompleteJob(Guid jobId, int newItems, int updatedItems, int skippedItems, int errorCount);
    
    /// <summary>
    /// Mark a job as failed.
    /// </summary>
    void FailJob(Guid jobId, string errorMessage);
}

/// <summary>
/// Singleton service that manages the library scan queue and processes scans sequentially.
/// </summary>
public class LibraryScanQueueService : BackgroundService, ILibraryScanQueueService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<LibraryScanQueueService> _logger;

    // Two-tier queue. Library scans and metadata refreshes are user-facing work and always
    // dequeue first; intro/credits detection is background housekeeping that nobody waits
    // on, so it only runs when the primary queue is empty. Without this split, a TV scan
    // finishing would append dozens of ffmpeg-heavy detection jobs and any scan requested
    // afterwards sat behind the whole backlog (FIFO priority inversion).
    private readonly ConcurrentQueue<LibraryScanJob> _queue = new();
    private readonly ConcurrentQueue<LibraryScanJob> _detectionQueue = new();
    private readonly ConcurrentDictionary<Guid, LibraryScanJob> _jobs = new();
    private LibraryScanJob? _currentJob;
    private readonly SemaphoreSlim _processingLock = new(1, 1);

    // Set while a detection job is running. Enqueueing any primary job cancels it: the
    // detection service checkpoints per episode (fingerprints save incrementally and
    // re-runs short-circuit finished episodes), so preempting loses at most one episode
    // of work and lets a scan start within seconds instead of minutes. The preempted
    // job goes back to the detection queue untouched. Volatile: written by the queue
    // loop, read by request threads calling PreemptRunningDetection.
    private volatile CancellationTokenSource? _detectionPreemptCts;

    /// <summary>
    /// True while any user-facing job is anywhere in the pipeline: queued, walking
    /// files, or holding its Metadata stage while enrichment drains. This is the
    /// single definition of "the user is waiting on something" — the dequeue gate
    /// and the Paused summary both use it, so the UI can never claim detection is
    /// paused while the loop actually runs it (or vice versa).
    /// </summary>
    private bool AnyPrimaryJobActive() => _jobs.Values.Any(j =>
        j.Type != LibraryScanJobType.IntroCreditsDetection &&
        (j.Status == LibraryScanStatus.Running || j.Status == LibraryScanStatus.Queued));

    // Makes each "already queued/running?" check + insert atomic. The concurrent collections
    // protect the individual operations, but the compound dedup check-then-add raced: two
    // concurrent enqueuers (e.g. the R-WI-008 scheduled sweep and an admin Run-now, or the
    // watcher vs a manual scan) could both pass the check and double-enqueue the same library,
    // causing a full duplicate scan and duplicate completion webhooks.
    private readonly object _enqueueLock = new();

    // Keep completed jobs for 5 minutes so the frontend can retrieve final status
    private readonly TimeSpan _completedJobRetention = TimeSpan.FromMinutes(5);

    // A job in the Metadata stage whose pending-enrichment count hasn't moved for this long
    // is finalized anyway (wedged provider), so "Running" can't outlive reality either.
    private static readonly TimeSpan MetadataStallTimeout = TimeSpan.FromMinutes(15);

    // jobId -> last time its MetadataRemaining changed (drain stall detection)
    private readonly ConcurrentDictionary<Guid, DateTime> _metadataProgressAt = new();

    // ── Intro/credits batch counters ─────────────────────────────────────────────
    // Detection runs as one queue job PER SERIES (dedup + sequencing need that), but a
    // fresh TV scan enqueues dozens at once and showing each as its own row swamps the
    // scan-status UI. These counters describe the current batch so GetAllJobs can
    // collapse the individual jobs into a single summary entry with real progress.
    // A "batch" is every detection job enqueued since the last time the backlog fully
    // drained. All mutations happen under _enqueueLock.
    private int _detectionBatchTotal;
    private int _detectionBatchDone;
    private int _detectionBatchFound;
    private int _detectionBatchFailed;
    private DateTime _detectionBatchStartedAt;
    private DateTime? _detectionBatchCompletedAt;

    /// <summary>Stable id for the synthetic summary row so clients can key on it.</summary>
    public static readonly Guid DetectionSummaryJobId = new("d47ec7ed-ba7c-4de7-ec71-0000de7ec710");

    private readonly IWebhookDispatcher _webhooks;
    private readonly IScheduledTaskRegistry? _registry;
    private readonly IMediaNotificationService? _notifications;
    private readonly IMetadataQueue? _metadataQueue;
    private readonly IImageDownloadQueue? _imageQueue;

    // BG-WI-005: optional so existing tests (and any headless composition) run ungated;
    // production DI always supplies it.
    private readonly Services.Transcoding.IPlaybackActivityService? _playbackActivity;

    /// <summary>BG-WI-005: how often a running detection job checks for newly-active
    /// playback. Settable so tests can exercise the preemption path without real 5 s
    /// waits (project convention; no InternalsVisibleTo).</summary>
    public TimeSpan DetectionPlaybackPollInterval { get; set; } = TimeSpan.FromSeconds(5);

    public LibraryScanQueueService(
        IServiceScopeFactory scopeFactory,
        ILogger<LibraryScanQueueService> logger,
        IWebhookDispatcher webhooks,
        IScheduledTaskRegistry? registry = null,
        IMediaNotificationService? notifications = null,
        IMetadataQueue? metadataQueue = null,
        IImageDownloadQueue? imageQueue = null,
        Services.Transcoding.IPlaybackActivityService? playbackActivity = null)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _webhooks = webhooks;
        _registry = registry;
        _notifications = notifications;
        _metadataQueue = metadataQueue;
        _imageQueue = imageQueue;
        _playbackActivity = playbackActivity;
    }

    /// <summary>
    /// Everything still in flight for this library after the file walk: items awaiting
    /// metadata enrichment PLUS artwork downloads awaiting the image queue. Image requests
    /// are enqueued inside enrichment (before its gauge decrements), so this combined
    /// count can't falsely hit zero during the hand-off between the two queues.
    /// </summary>
    private int GetPendingEnrichmentCount(Guid libraryId)
        => (_metadataQueue?.GetPendingCountForLibrary(libraryId) ?? 0)
         + (_imageQueue?.GetPendingCountForLibrary(libraryId) ?? 0);

    public LibraryScanJob EnqueueScan(Guid libraryId, string libraryName)
    {
        lock (_enqueueLock)
        {
            // Dedup against QUEUED jobs only (atomic with the insert below). A scan
            // that is already RUNNING may have passed the directory a new request is
            // about (R-WI-019 review: an *arr import landing mid-scan was silently
            // coalesced into a scan that had already walked past it, and the file
            // stayed missing until the next scheduled sweep) — so one follow-up job
            // is allowed behind a running scan. Repeat requests then dedup against
            // that queued follow-up, bounding the backlog to one job per library.
            var existingJob = _jobs.Values.FirstOrDefault(j =>
                j.LibraryId == libraryId &&
                j.Type == LibraryScanJobType.LibraryScan &&
                j.Status == LibraryScanStatus.Queued);

            if (existingJob != null)
            {
                _logger.LogInformation("Library {LibraryId} is already in queue, returning existing job", libraryId);
                return existingJob;
            }

            var job = new LibraryScanJob
            {
                Type = LibraryScanJobType.LibraryScan,
                LibraryId = libraryId,
                LibraryName = libraryName,
                Status = LibraryScanStatus.Queued,
                Stage = LibraryScanStage.Pending,
                StartedAt = DateTime.UtcNow
            };

            return EnqueueJob(job);
        }
    }

    public LibraryScanJob EnqueueMetadataRefresh()
    {
        lock (_enqueueLock)
        {
            // Check if Metadata Refresh is already running/queued (atomic with the insert)
            var existingJob = _jobs.Values.FirstOrDefault(j =>
                j.Type == LibraryScanJobType.MetadataRefresh &&
                (j.Status == LibraryScanStatus.Queued || j.Status == LibraryScanStatus.Running));

            if (existingJob != null)
            {
                _logger.LogInformation("Metadata refresh is already in queue/running, returning existing job");
                return existingJob;
            }

            var job = new LibraryScanJob
            {
                Type = LibraryScanJobType.MetadataRefresh,
                LibraryId = Guid.Empty, // Global job
                LibraryName = "Metadata Refresh",
                Status = LibraryScanStatus.Queued,
                Stage = LibraryScanStage.Pending,
                StartedAt = DateTime.UtcNow
            };

            return EnqueueJob(job);
        }
    }

    public LibraryScanJob EnqueueIntroCreditsDetection(Guid seriesId, string seriesName)
    {
        lock (_enqueueLock)
        {
            // Dedup: at most one detection job per series queued or running at a time
            // (atomic with the insert).
            var existingJob = _jobs.Values.FirstOrDefault(j =>
                j.Type == LibraryScanJobType.IntroCreditsDetection &&
                j.TargetSeriesId == seriesId &&
                (j.Status == LibraryScanStatus.Queued || j.Status == LibraryScanStatus.Running));

            if (existingJob != null)
            {
                return existingJob;
            }

            // Batch bookkeeping: a fully drained batch means this enqueue starts a new one.
            if (_detectionBatchTotal > 0 && _detectionBatchDone >= _detectionBatchTotal)
            {
                _detectionBatchTotal = 0;
                _detectionBatchDone = 0;
                _detectionBatchFound = 0;
                _detectionBatchFailed = 0;
                _detectionBatchCompletedAt = null;
            }
            if (_detectionBatchTotal == 0) _detectionBatchStartedAt = DateTime.UtcNow;
            _detectionBatchTotal++;

            var job = new LibraryScanJob
            {
                Type = LibraryScanJobType.IntroCreditsDetection,
                LibraryId = Guid.Empty,
                LibraryName = $"Intro/Credits: {seriesName}",
                TargetSeriesId = seriesId,
                Status = LibraryScanStatus.Queued,
                Stage = LibraryScanStage.Pending,
                StartedAt = DateTime.UtcNow
            };

            return EnqueueJob(job);
        }
    }

    private LibraryScanJob EnqueueJob(LibraryScanJob job)
    {
        _jobs[job.Id] = job;
        if (job.Type == LibraryScanJobType.IntroCreditsDetection)
        {
            _detectionQueue.Enqueue(job);
        }
        else
        {
            _queue.Enqueue(job);
            // User-facing work preempts background fingerprinting immediately.
            PreemptRunningDetection();
        }
        UpdateQueuePositions();
        _logger.LogInformation("Enqueued job: {Type} for {Name} (Job ID: {JobId})", job.Type, job.LibraryName, job.Id);
        // Announce immediately so connected clients show the queued scan without
        // waiting for its first progress report (which may be minutes away if
        // another scan is running).
        PushScanNotification(job, 0, 0, "Queued", "Pending");
        return job;
    }

    public LibraryScanJob? GetJobStatus(Guid jobId)
    {
        return _jobs.TryGetValue(jobId, out var job) ? job : null;
    }

    public IEnumerable<LibraryScanJob> GetAllJobs()
    {
        var visible = _jobs.Values
            .Where(j => j.Status == LibraryScanStatus.Running ||
                       j.Status == LibraryScanStatus.Queued ||
                       (j.CompletedAt.HasValue && DateTime.UtcNow - j.CompletedAt.Value < _completedJobRetention))
            .ToList();

        // Collapse the per-series intro/credits jobs into one summary row — a fresh TV
        // scan enqueues one job per show, which would otherwise swamp the status UI.
        // Individual jobs remain addressable via GetJobStatus for per-series callers.
        var result = visible.Where(j => j.Type != LibraryScanJobType.IntroCreditsDetection).ToList();
        var summary = BuildDetectionSummary(
            visible.Where(j => j.Type == LibraryScanJobType.IntroCreditsDetection), AnyPrimaryJobActive());
        if (summary != null) result.Add(summary);

        // Return jobs sorted by status (Running first, then Queued/Paused, then recent Completed)
        return result
            .OrderBy(j => j.Status == LibraryScanStatus.Running ? 0 :
                         j.Status is LibraryScanStatus.Queued or LibraryScanStatus.Paused ? 1 : 2)
            .ThenBy(j => j.QueuePosition)
            .ThenByDescending(j => j.StartedAt)
            .ToList();
    }

    /// <summary>
    /// One synthetic job describing the whole intro/credits batch: ProcessedFiles/TotalFiles
    /// carry batch progress, CurrentFile the series being fingerprinted right now, and
    /// UpdatedItems the intros+credits found so far. Null when no batch is active or recent.
    /// Reports Paused while primary work (scans/refreshes) has the queue — detection
    /// yields to those and resumes automatically.
    /// </summary>
    private LibraryScanJob? BuildDetectionSummary(IEnumerable<LibraryScanJob> visibleDetections, bool primaryBusy)
    {
        int total, done, found, failed;
        DateTime startedAt;
        DateTime? completedAt;
        lock (_enqueueLock)
        {
            total = _detectionBatchTotal;
            done = _detectionBatchDone;
            found = _detectionBatchFound;
            failed = _detectionBatchFailed;
            startedAt = _detectionBatchStartedAt;
            completedAt = _detectionBatchCompletedAt;
        }

        if (total == 0) return null;

        var batchActive = done < total;
        if (!batchActive && (completedAt == null || DateTime.UtcNow - completedAt.Value >= _completedJobRetention))
        {
            return null;
        }

        var running = visibleDetections.FirstOrDefault(j => j.Status == LibraryScanStatus.Running);
        const string namePrefix = "Intro/Credits: ";
        var currentSeries = running?.LibraryName is { } n && n.StartsWith(namePrefix, StringComparison.Ordinal)
            ? n[namePrefix.Length..]
            : running?.LibraryName;

        return new LibraryScanJob
        {
            Id = DetectionSummaryJobId,
            Type = LibraryScanJobType.IntroCreditsDetection,
            LibraryId = Guid.Empty,
            LibraryName = "Intro/Credits Detection",
            Status = running != null ? LibraryScanStatus.Running
                   : batchActive && primaryBusy ? LibraryScanStatus.Paused
                   : batchActive ? LibraryScanStatus.Queued
                   : LibraryScanStatus.Completed,
            Stage = batchActive ? LibraryScanStage.Processing : LibraryScanStage.Finishing,
            TotalFiles = total,
            ProcessedFiles = done,
            UpdatedItems = found,
            ErrorCount = failed,
            CurrentFile = currentSeries,
            StartedAt = startedAt,
            CompletedAt = batchActive ? null : completedAt,
            QueuePosition = 0
        };
    }

    public bool IsLibraryInQueue(Guid libraryId)
    {
        return _jobs.Values.Any(j => 
            j.LibraryId == libraryId && 
            j.Type == LibraryScanJobType.LibraryScan &&
            (j.Status == LibraryScanStatus.Queued || j.Status == LibraryScanStatus.Running));
    }
    public void UpdateProgress(Guid jobId, LibraryScanStage stage, int processedFiles, int totalFiles,
        string? currentFile = null, int newItems = 0, int updatedItems = 0, int skippedItems = 0)
    {
        if (_jobs.TryGetValue(jobId, out var job))
        {
            job.Stage = stage;
            job.ProcessedFiles = processedFiles;
            job.TotalFiles = totalFiles;
            job.CurrentFile = currentFile;
            job.NewItems = newItems;
            job.UpdatedItems = updatedItems;
            job.SkippedItems = skippedItems;
        }
    }

    public void CompleteJob(Guid jobId, int newItems, int updatedItems, int skippedItems, int errorCount)
    {
        if (_jobs.TryGetValue(jobId, out var job))
        {
            ReportCatalogResult(job, newItems, updatedItems, skippedItems, errorCount);
            FinalizeJob(job);
            if (job.Type == LibraryScanJobType.IntroCreditsDetection)
            {
                RecordDetectionOutcome(foundItems: updatedItems, failed: false);
            }
        }
    }

    private void RecordDetectionOutcome(int foundItems, bool failed)
    {
        lock (_enqueueLock)
        {
            _detectionBatchDone++;
            _detectionBatchFound += foundItems;
            if (failed) _detectionBatchFailed++;
            if (_detectionBatchDone >= _detectionBatchTotal)
            {
                _detectionBatchCompletedAt = DateTime.UtcNow;
            }
        }
    }

    /// <summary>
    /// Fires the completion webhook + task telemetry for a job whose catalog work just
    /// finished. Runs exactly once per job, whether it completes immediately or first
    /// passes through the Metadata stage.
    /// </summary>
    private void ReportCatalogResult(LibraryScanJob job, int newItems, int updatedItems, int skippedItems, int errorCount)
    {
        job.NewItems = newItems;
        job.UpdatedItems = updatedItems;
        job.SkippedItems = skippedItems;
        job.ErrorCount = errorCount;

        _logger.LogInformation(
            "Scan completed for {LibraryName}: {New} new, {Updated} updated, {Skipped} skipped, {Errors} errors",
            job.LibraryName, newItems, updatedItems, skippedItems, errorCount);

        // Event-driven task telemetry: surface that the scan queue just finished a job.
        _registry?.Report(ScheduledTaskNames.LibraryScanQueue, errorCount > 0 ? "Failed" : "Success");

        // P2-WI-004: fan out a webhook. newItems stands in for "media.added" (which
        // has no clean per-item hook — see phase-2 rescope), so subscribers learn
        // how much arrived without us wiring a per-item event.
        _webhooks.Enqueue(new WebhookEvent(Models.WebhookEvents.LibraryScanCompleted, new
        {
            libraryId = job.LibraryId,
            libraryName = job.LibraryName,
            newItems,
            updatedItems,
            skippedItems,
            errorCount,
        }));
    }

    /// <summary>
    /// Terminal state flip. No webhook here — that fired in <see cref="ReportCatalogResult"/>.
    /// </summary>
    private void FinalizeJob(LibraryScanJob job)
    {
        job.Status = LibraryScanStatus.Completed;
        job.Stage = LibraryScanStage.Finishing;
        job.MetadataRemaining = 0;
        job.CompletedAt = DateTime.UtcNow;
        job.QueuePosition = 0;
        job.CurrentFile = null;
        _metadataProgressAt.TryRemove(job.Id, out _);
        PushScanNotification(job, job.ProcessedFiles, job.TotalFiles, "Complete", "Complete");
    }

    /// <summary>
    /// Move a library-scan job into the Metadata stage: the file walk is done (webhook and
    /// counters already reported), but enrichment for this library is still draining, so the
    /// job stays Running until the pending gauge hits zero (or stalls).
    /// </summary>
    private void BeginMetadataStage(LibraryScanJob job, int pendingEnrichment)
    {
        job.Stage = LibraryScanStage.Metadata;
        job.MetadataTotal = pendingEnrichment;
        job.MetadataRemaining = pendingEnrichment;
        job.CurrentFile = null;
        _metadataProgressAt[job.Id] = DateTime.UtcNow;
        _logger.LogInformation("Scan of {LibraryName} entering Metadata stage: {Pending} enrichment/artwork tasks pending",
            job.LibraryName, pendingEnrichment);
        PushScanNotification(job, 0, pendingEnrichment, $"Enriching metadata & artwork ({pendingEnrichment} remaining)", "Metadata");
    }

    public void FailJob(Guid jobId, string errorMessage)
    {
        if (_jobs.TryGetValue(jobId, out var job))
        {
            if (job.Type == LibraryScanJobType.IntroCreditsDetection)
            {
                RecordDetectionOutcome(foundItems: 0, failed: true);
            }
            job.Status = LibraryScanStatus.Failed;
            job.ErrorMessage = errorMessage;
            job.CompletedAt = DateTime.UtcNow;
            job.QueuePosition = 0;
            job.CurrentFile = null;
            _metadataProgressAt.TryRemove(job.Id, out _);

            _logger.LogError("Scan failed for {LibraryName}: {Error}", job.LibraryName, errorMessage);
            _registry?.Report(ScheduledTaskNames.LibraryScanQueue, "Failed", error: errorMessage);
            PushScanNotification(job, job.ProcessedFiles, job.TotalFiles, errorMessage, "Failed");

            _webhooks.Enqueue(new WebhookEvent(Models.WebhookEvents.LibraryScanFailed, new
            {
                libraryId = job.LibraryId,
                libraryName = job.LibraryName,
                error = errorMessage,
            }));
        }
    }

    /// <summary>
    /// BG-WI-005/008: should a queued detection job stay parked because someone is
    /// watching? False fast when playback is idle; when active, consult the
    /// DetectionDuringPlayback setting (default true = run alongside streams — detection
    /// is audio-only and BelowNormal, so only HDD disk contention justifies pausing).
    /// SettingsService caches reads (60 s TTL), so polling this from the 500 ms queue
    /// loop is cheap; the scope is only created while playback is actually active.
    /// </summary>
    private async Task<bool> ShouldDeferDetectionForPlaybackAsync()
    {
        if (_playbackActivity == null || !_playbackActivity.IsPlaybackActive) return false;

        using var scope = _scopeFactory.CreateScope();
        var settings = scope.ServiceProvider.GetService<ISettingsService>();
        if (settings == null) return false; // no settings source → behave like the default (run)
        return !await settings.GetSettingAsync("DetectionDuringPlayback", true);
    }

    /// <summary>
    /// Cancel the currently-running detection job, if any. Safe to call from any thread;
    /// the race where the job finishes (and disposes its CTS) concurrently is benign.
    /// </summary>
    private void PreemptRunningDetection()
    {
        var cts = _detectionPreemptCts;
        if (cts == null) return;
        try { cts.Cancel(); }
        catch (ObjectDisposedException) { /* job just finished on its own */ }
    }

    /// <summary>
    /// Push a scan status snapshot over SignalR. Library scans only — global jobs
    /// (metadata refresh, intro detection) have no library to attribute the toast to.
    /// </summary>
    private void PushScanNotification(LibraryScanJob job, int processed, int total, string status, string stage)
    {
        if (job.Type != LibraryScanJobType.LibraryScan) return;
        _notifications?.NotifyScanProgress(job.LibraryId, processed, total, status, stage);
    }

    private void UpdateQueuePositions()
    {
        // Mirror actual dequeue order: primary jobs first, detection housekeeping last.
        var queuedJobs = _jobs.Values
            .Where(j => j.Status == LibraryScanStatus.Queued)
            .OrderBy(j => j.Type == LibraryScanJobType.IntroCreditsDetection ? 1 : 0)
            .ThenBy(j => j.StartedAt)
            .ToList();

        for (int i = 0; i < queuedJobs.Count; i++)
        {
            queuedJobs[i].QueuePosition = i + 1;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Library Scan Queue Service started");

        // Independent drain watcher: the main loop below blocks for the whole duration of a
        // scan, so a previous job sitting in its Metadata stage must be finalized elsewhere.
        var drainMonitor = Task.Run(() => MonitorMetadataDrainAsync(stoppingToken), CancellationToken.None);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Clean up old completed jobs
                CleanupOldJobs();

                // Process next job: primary queue (scans, refreshes) always wins.
                // Detection only runs when NO primary job is active anywhere in the
                // pipeline — including scans holding their Metadata stage (still
                // "Running" to the user while enrichment/artwork drains). Without
                // that check the loop considered itself idle the moment a scan's
                // file walk ended and started fingerprinting alongside it.
                // BG-WI-005: detection additionally waits for playback to go idle —
                // fingerprinting is deferrable housekeeping, viewers are not.
                // BG-WI-008: gated by the DetectionDuringPlayback setting (default on =
                // run alongside streams; detection is audio-only and BelowNormal).
                LibraryScanJob? job = null;
                if (_queue.TryDequeue(out var primaryJob))
                {
                    job = primaryJob;
                }
                else if (!AnyPrimaryJobActive()
                    && !await ShouldDeferDetectionForPlaybackAsync()
                    && _detectionQueue.TryDequeue(out var detectionJob))
                {
                    job = detectionJob;
                }

                if (job != null)
                {
                    await _processingLock.WaitAsync(stoppingToken);
                    try
                    {
                        _currentJob = job;
                        job.Status = LibraryScanStatus.Running;
                        job.QueuePosition = 0;
                        UpdateQueuePositions();

                        await ProcessScanJobAsync(job, stoppingToken);
                    }
                    finally
                    {
                        _currentJob = null;
                        _processingLock.Release();
                    }
                }
                else
                {
                    // No jobs in queue, wait a bit before checking again
                    await Task.Delay(500, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in scan queue processing loop");
                await Task.Delay(1000, stoppingToken);
            }
        }

        _logger.LogInformation("Library Scan Queue Service stopped");
        await drainMonitor;
    }

    /// <summary>
    /// Finalizes library-scan jobs sitting in the Metadata stage: updates their remaining
    /// count from the per-library enrichment gauge, completes them when it drains, and
    /// completes them anyway (with a warning) if the count stalls for <see cref="MetadataStallTimeout"/>.
    /// </summary>
    private async Task MonitorMetadataDrainAsync(CancellationToken stoppingToken)
    {
        if (_metadataQueue == null && _imageQueue == null) return;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                foreach (var job in _jobs.Values.Where(j =>
                    j.Type == LibraryScanJobType.LibraryScan &&
                    j.Status == LibraryScanStatus.Running &&
                    j.Stage == LibraryScanStage.Metadata))
                {
                    var pending = GetPendingEnrichmentCount(job.LibraryId);

                    if (pending != job.MetadataRemaining)
                    {
                        job.MetadataRemaining = pending;
                        // The watcher can enqueue more work mid-drain; keep total >= remaining.
                        job.MetadataTotal = Math.Max(job.MetadataTotal, pending);
                        _metadataProgressAt[job.Id] = DateTime.UtcNow;
                        PushScanNotification(job, job.MetadataTotal - pending, job.MetadataTotal,
                            $"Enriching metadata & artwork ({pending} remaining)", "Metadata");
                    }

                    if (pending == 0)
                    {
                        _logger.LogInformation("Metadata enrichment drained for {LibraryName}; scan job finalized", job.LibraryName);
                        FinalizeJob(job);
                    }
                    else if (_metadataProgressAt.TryGetValue(job.Id, out var lastChange) &&
                             DateTime.UtcNow - lastChange > MetadataStallTimeout)
                    {
                        _logger.LogWarning(
                            "Metadata enrichment for {LibraryName} stalled at {Pending} pending items for {Minutes} minutes; finalizing scan job anyway",
                            job.LibraryName, pending, (int)MetadataStallTimeout.TotalMinutes);
                        FinalizeJob(job);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in metadata drain monitor");
            }

            try { await Task.Delay(1000, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task ProcessScanJobAsync(LibraryScanJob job, CancellationToken stoppingToken)
    {
        _logger.LogInformation("Starting job: {Type} - {Name} (Job ID: {JobId})", job.Type, job.LibraryName, job.Id);

        try
        {
            using var scope = _scopeFactory.CreateScope();

            if (job.Type == LibraryScanJobType.LibraryScan)
            {
                // Use new scanner orchestrator with progress adapter
                var orchestrator = scope.ServiceProvider.GetRequiredService<IScannerOrchestrator>();

                // Create synchronous progress adapter (Progress<T> is async and may not fire before CompleteJob)
                var lastProgress = new ScanProgress(0, 0, null, "Starting");
                var progress = new SyncProgress<ScanProgress>(p =>
                {
                    job.Stage = p.Stage;
                    job.ProcessedFiles = p.ProcessedCount;
                    job.TotalFiles = p.TotalCount;
                    job.CurrentFile = p.CurrentFileName;
                    job.NewItems = p.NewCount;
                    job.UpdatedItems = p.UpdatedCount;
                    job.SkippedItems = p.SkippedCount;
                    job.ErrorCount = p.ErrorCount;
                    lastProgress = p;
                    PushScanNotification(job, p.ProcessedCount, p.TotalCount, p.CurrentPhase, p.Stage.ToString());
                });

                await orchestrator.ExecuteScanAsync(job.LibraryId, progress, stoppingToken);

                // Catalog work is done: report results (webhook, telemetry, final counters)
                // using the LAST captured progress (guaranteed synchronous). If enrichment
                // for this library is still pending, hold the job open in the Metadata
                // stage — the drain monitor finalizes it; otherwise finalize now.
                ReportCatalogResult(job, lastProgress.NewCount, lastProgress.UpdatedCount,
                    lastProgress.SkippedCount, lastProgress.ErrorCount);
                var pendingEnrichment = GetPendingEnrichmentCount(job.LibraryId);
                if (pendingEnrichment > 0)
                {
                    BeginMetadataStage(job, pendingEnrichment);
                }
                else
                {
                    FinalizeJob(job);
                }

                // Update Currently Added Cache
                try
                {
                    var libraryService = scope.ServiceProvider.GetRequiredService<ILibraryService>();
                    await libraryService.UpdateRecentlyAddedCacheAsync(job.LibraryId);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to update recently added cache for library {LibraryId}", job.LibraryId);
                }

                // Auto-enqueue intro/credits detection for every series in the scanned
                // library that has ≥2 episodes. The detection service short-circuits on
                // series whose fingerprints are already populated, so this is cheap on
                // a stable library and only does real work when episodes were added.
                try
                {
                    await EnqueueDetectionForLibraryAsync(scope.ServiceProvider, job.LibraryId, stoppingToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to enqueue intro/credits detection for library {LibraryId}", job.LibraryId);
                }
            }
            else if (job.Type == LibraryScanJobType.MetadataRefresh)
            {
                var refreshService = scope.ServiceProvider.GetRequiredService<MetadataRefreshService>();
                // We'll need to expose a method in MetadataRefreshService that takes the Job
                await refreshService.RunRefreshJobAsync(job, stoppingToken);
            }
            else if (job.Type == LibraryScanJobType.IntroCreditsDetection)
            {
                if (job.TargetSeriesId == null)
                {
                    FailJob(job.Id, "IntroCreditsDetection job has no TargetSeriesId.");
                    return;
                }

                var detector = scope.ServiceProvider.GetRequiredService<IIntroCreditsDetectionService>();
                var preemptCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                _detectionPreemptCts = preemptCts;
                // Close the enqueue race: a primary job that arrived between this job's
                // dequeue and the line above found no CTS to cancel. Now that it exists,
                // honour any waiting primary work immediately.
                if (!_queue.IsEmpty)
                {
                    preemptCts.Cancel();
                }

                // BG-WI-005: a viewer pressing Play mid-detection preempts it exactly
                // like a scan would — same CTS, same requeue, same checkpoint-resume.
                // Polling (5s) is fine: the cost being deferred is minutes long.
                // BG-WI-008: the shared helper also consults DetectionDuringPlayback
                // per poll (cached reads), so flipping the setting mid-run takes effect.
                Task? playbackWatch = null;
                if (_playbackActivity != null)
                {
                    var watchToken = preemptCts.Token;
                    playbackWatch = Task.Run(async () =>
                    {
                        try
                        {
                            while (!watchToken.IsCancellationRequested)
                            {
                                await Task.Delay(DetectionPlaybackPollInterval, watchToken);
                                if (await ShouldDeferDetectionForPlaybackAsync())
                                {
                                    _logger.LogInformation("Playback became active; deferring intro/credits detection");
                                    try { preemptCts.Cancel(); }
                                    catch (ObjectDisposedException) { /* job just finished */ }
                                    return;
                                }
                            }
                        }
                        catch (OperationCanceledException) { /* job finished or was preempted */ }
                    }, CancellationToken.None);
                }

                try
                {
                    var result = await detector.DetectAsync(job.TargetSeriesId.Value, preemptCts.Token);
                    CompleteJob(job.Id, newItems: 0, updatedItems: result.IntrosFound + result.CreditsFound, skippedItems: 0, errorCount: 0);
                }
                catch (OperationCanceledException) when (preemptCts.IsCancellationRequested && !stoppingToken.IsCancellationRequested)
                {
                    // Preempted by a scan/refresh (or active playback, BG-WI-005): not a
                    // failure. Re-queue the SAME job (batch counters untouched — it's
                    // still one pending series) so it resumes once the queue is idle
                    // again; already-fingerprinted episodes are skipped on the re-run.
                    lock (_enqueueLock)
                    {
                        job.Status = LibraryScanStatus.Queued;
                        job.Stage = LibraryScanStage.Pending;
                        _detectionQueue.Enqueue(job);
                        UpdateQueuePositions();
                    }
                    _logger.LogInformation("Preempted intro/credits detection for {Name}; re-queued behind primary jobs/playback", job.LibraryName);
                }
                finally
                {
                    _detectionPreemptCts = null;
                    // Unblock the playback watcher's Task.Delay BEFORE disposing the CTS —
                    // disposing first would freeze the token and leak the watcher loop.
                    try { preemptCts.Cancel(); } catch { /* already cancelled/disposed */ }
                    if (playbackWatch != null) { try { await playbackWatch; } catch { /* watcher never throws */ } }
                    preemptCts.Dispose();
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing job {Name}", job.LibraryName);
            FailJob(job.Id, ex.Message);
        }
    }

    /// <summary>
    /// Synchronous IProgress implementation to ensure callbacks run immediately.
    /// </summary>
    private class SyncProgress<T> : IProgress<T>
    {
        private readonly Action<T> _handler;
        public SyncProgress(Action<T> handler) => _handler = handler;
        public void Report(T value) => _handler(value);
    }

    /// <summary>
    /// Enqueue an intro/credits detection job for every series in the given library
    /// that has ≥2 episodes. Skipped entirely when both AutoDetectIntros and
    /// AutoDetectCredits are disabled.
    /// </summary>
    private async Task EnqueueDetectionForLibraryAsync(IServiceProvider scopeServices, Guid libraryId, CancellationToken ct)
    {
        var settings = scopeServices.GetRequiredService<ISettingsService>();
        var detectIntros = await settings.GetSettingAsync("AutoDetectIntros", true);
        var detectCredits = await settings.GetSettingAsync("AutoDetectCredits", true);
        if (!detectIntros && !detectCredits) return;

        var db = scopeServices.GetRequiredService<AppDbContext>();
        var seriesIds = await db.MediaItems
            .Where(m => m.LibraryId == libraryId
                && m.Type == MediaType.Series
                && db.MediaItems.Count(e => e.SeriesId == m.Id && e.Type == MediaType.Episode) >= 2)
            .Select(m => new { m.Id, m.Title })
            .ToListAsync(ct);

        foreach (var series in seriesIds)
        {
            EnqueueIntroCreditsDetection(series.Id, series.Title);
        }
    }

    private void CleanupOldJobs()
    {
        var cutoff = DateTime.UtcNow - _completedJobRetention;
        var oldJobs = _jobs.Values
            .Where(j => j.CompletedAt.HasValue && j.CompletedAt.Value < cutoff)
            .Select(j => j.Id)
            .ToList();

        foreach (var jobId in oldJobs)
        {
            _jobs.TryRemove(jobId, out _);
        }
    }
}
