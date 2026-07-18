using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using SoftMedia.Server.Data;
using SoftMedia.Server.Models;
using SoftMedia.Server.Services.Abstractions;
using SoftMedia.Server.Services.Infrastructure;
using SoftMedia.Server.Services.Media.Detection;

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
    private readonly ConcurrentQueue<LibraryScanJob> _queue = new();
    private readonly ConcurrentDictionary<Guid, LibraryScanJob> _jobs = new();
    private LibraryScanJob? _currentJob;
    private readonly SemaphoreSlim _processingLock = new(1, 1);

    // Makes each "already queued/running?" check + insert atomic. The concurrent collections
    // protect the individual operations, but the compound dedup check-then-add raced: two
    // concurrent enqueuers (e.g. the R-WI-008 scheduled sweep and an admin Run-now, or the
    // watcher vs a manual scan) could both pass the check and double-enqueue the same library,
    // causing a full duplicate scan and duplicate completion webhooks.
    private readonly object _enqueueLock = new();

    // Keep completed jobs for 5 minutes so the frontend can retrieve final status
    private readonly TimeSpan _completedJobRetention = TimeSpan.FromMinutes(5);

    private readonly IWebhookDispatcher _webhooks;
    private readonly IScheduledTaskRegistry? _registry;

    public LibraryScanQueueService(
        IServiceScopeFactory scopeFactory,
        ILogger<LibraryScanQueueService> logger,
        IWebhookDispatcher webhooks,
        IScheduledTaskRegistry? registry = null)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _webhooks = webhooks;
        _registry = registry;
    }

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
        _queue.Enqueue(job);
        UpdateQueuePositions();
        _logger.LogInformation("Enqueued job: {Type} for {Name} (Job ID: {JobId})", job.Type, job.LibraryName, job.Id);
        return job;
    }

    public LibraryScanJob? GetJobStatus(Guid jobId)
    {
        return _jobs.TryGetValue(jobId, out var job) ? job : null;
    }

    public IEnumerable<LibraryScanJob> GetAllJobs()
    {
        // Return jobs sorted by status (Running first, then Queued, then recent Completed)
        return _jobs.Values
            .Where(j => j.Status == LibraryScanStatus.Running || 
                       j.Status == LibraryScanStatus.Queued ||
                       (j.CompletedAt.HasValue && DateTime.UtcNow - j.CompletedAt.Value < _completedJobRetention))
            .OrderBy(j => j.Status == LibraryScanStatus.Running ? 0 : 
                         j.Status == LibraryScanStatus.Queued ? 1 : 2)
            .ThenBy(j => j.QueuePosition)
            .ThenByDescending(j => j.StartedAt)
            .ToList();
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
            job.Status = LibraryScanStatus.Completed;
            job.Stage = LibraryScanStage.Finishing;
            job.NewItems = newItems;
            job.UpdatedItems = updatedItems;
            job.SkippedItems = skippedItems;
            job.ErrorCount = errorCount;
            job.CompletedAt = DateTime.UtcNow;
            job.QueuePosition = 0;
            job.CurrentFile = null;
            
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
    }

    public void FailJob(Guid jobId, string errorMessage)
    {
        if (_jobs.TryGetValue(jobId, out var job))
        {
            job.Status = LibraryScanStatus.Failed;
            job.ErrorMessage = errorMessage;
            job.CompletedAt = DateTime.UtcNow;
            job.QueuePosition = 0;
            job.CurrentFile = null;
            
            _logger.LogError("Scan failed for {LibraryName}: {Error}", job.LibraryName, errorMessage);
            _registry?.Report(ScheduledTaskNames.LibraryScanQueue, "Failed", error: errorMessage);

            _webhooks.Enqueue(new WebhookEvent(Models.WebhookEvents.LibraryScanFailed, new
            {
                libraryId = job.LibraryId,
                libraryName = job.LibraryName,
                error = errorMessage,
            }));
        }
    }

    private void UpdateQueuePositions()
    {
        var queuedJobs = _jobs.Values
            .Where(j => j.Status == LibraryScanStatus.Queued)
            .OrderBy(j => j.StartedAt)
            .ToList();

        for (int i = 0; i < queuedJobs.Count; i++)
        {
            queuedJobs[i].QueuePosition = i + 1;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Library Scan Queue Service started");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Clean up old completed jobs
                CleanupOldJobs();

                // Process next job in queue
                if (_queue.TryDequeue(out var job))
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
                    job.Stage = LibraryScanStage.Processing;
                    job.ProcessedFiles = p.ProcessedCount;
                    job.TotalFiles = p.TotalCount;
                    job.CurrentFile = p.CurrentFileName;
                    job.NewItems = p.NewCount;
                    job.UpdatedItems = p.UpdatedCount;
                    job.SkippedItems = p.SkippedCount;
                    lastProgress = p;
                });
                
                await orchestrator.ExecuteScanAsync(job.LibraryId, progress, stoppingToken);

                // Mark as complete using the LAST captured progress (guaranteed synchronous)
                CompleteJob(job.Id, lastProgress.NewCount, lastProgress.UpdatedCount, lastProgress.SkippedCount, job.ErrorCount);

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
                var result = await detector.DetectAsync(job.TargetSeriesId.Value, stoppingToken);
                CompleteJob(job.Id, newItems: 0, updatedItems: result.IntrosFound + result.CreditsFound, skippedItems: 0, errorCount: 0);
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
