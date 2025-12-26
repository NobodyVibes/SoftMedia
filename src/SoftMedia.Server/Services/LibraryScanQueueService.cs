using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using SoftMedia.Server.Data;
using SoftMedia.Server.Models;

namespace SoftMedia.Server.Services;

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

    // Keep completed jobs for 5 minutes so the frontend can retrieve final status
    private readonly TimeSpan _completedJobRetention = TimeSpan.FromMinutes(5);

    public LibraryScanQueueService(
        IServiceScopeFactory scopeFactory,
        ILogger<LibraryScanQueueService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public LibraryScanJob EnqueueScan(Guid libraryId, string libraryName)
    {
        // Check if already queued or running
        if (IsLibraryInQueue(libraryId))
        {
            var existingJob = _jobs.Values.FirstOrDefault(j => 
                j.LibraryId == libraryId && 
                (j.Status == LibraryScanStatus.Queued || j.Status == LibraryScanStatus.Running));
            
            if (existingJob != null)
            {
                _logger.LogInformation("Library {LibraryId} is already in queue, returning existing job", libraryId);
                return existingJob;
            }
        }

        var job = new LibraryScanJob
        {
            LibraryId = libraryId,
            LibraryName = libraryName,
            Status = LibraryScanStatus.Queued,
            Stage = LibraryScanStage.Pending,
            StartedAt = DateTime.UtcNow
        };

        _jobs[job.Id] = job;
        _queue.Enqueue(job);
        
        UpdateQueuePositions();
        
        _logger.LogInformation("Enqueued scan for library {LibraryName} (Job ID: {JobId})", libraryName, job.Id);
        
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
        _logger.LogInformation("Starting scan for library: {LibraryName} (Job ID: {JobId})", job.LibraryName, job.Id);

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var fileScanner = scope.ServiceProvider.GetRequiredService<IFileScannerService>();

            // Call the scanner with progress reporting
            await fileScanner.ScanLibraryWithProgressAsync(job.LibraryId, job);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error scanning library {LibraryName}", job.LibraryName);
            FailJob(job.Id, ex.Message);
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
