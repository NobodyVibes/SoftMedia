using System.Collections.Concurrent;

using SoftMedia.Server.Services.Transcoding.Models;

namespace SoftMedia.Server.Services.Infrastructure;

/// <summary>
/// Background service that monitors transcode sessions and handles:
/// - State transitions (BURST → CATCHING → CRUISING → DORMANT)
/// - Disk pressure eviction
/// - Stale session cleanup
/// - Crash detection and recovery
/// </summary>
public class ThrottleMonitorService : BackgroundService
{
    private readonly TranscodeService _transcodeService;
    private readonly ILogger<ThrottleMonitorService> _logger;

    // Timing intervals
    private const int StateCheckIntervalMs = 5000;       // 5 seconds
    private const int DiskCheckIntervalMs = 30000;       // 30 seconds
    private const int StaleCheckIntervalMs = 3600000;    // 60 minutes

    // Throttling thresholds (from plan)
    private const int MinDiskSpaceThresholdMB = 500;
    private const int MaxDormantAgeHours = 24;
    private const int ClientInactivityTimeoutSeconds = 90;  // Stop FFmpeg if no segment requests for 90s (accounts for HLS buffering)

    private DateTime _lastDiskCheck = DateTime.MinValue;
    private DateTime _lastStaleCheck = DateTime.MinValue;

    public ThrottleMonitorService(TranscodeService transcodeService, ILogger<ThrottleMonitorService> logger)
    {
        _transcodeService = transcodeService;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("ThrottleMonitorService started");

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    // High-frequency: State transitions and crash detection (every 5s)
                    await ProcessStateTransitionsAsync();

                    // Medium-frequency: Disk pressure check (every 30s)
                    if ((DateTime.UtcNow - _lastDiskCheck).TotalMilliseconds >= DiskCheckIntervalMs)
                    {
                        await CheckDiskPressureAsync();
                        _lastDiskCheck = DateTime.UtcNow;
                    }

                    // Low-frequency: Stale session cleanup (every 60min)
                    if ((DateTime.UtcNow - _lastStaleCheck).TotalMilliseconds >= StaleCheckIntervalMs)
                    {
                        CleanupStaleSessions();
                        _lastStaleCheck = DateTime.UtcNow;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error in ThrottleMonitorService loop");
                }

                await Task.Delay(StateCheckIntervalMs, stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Cooperative shutdown — host cancelled. Exit cleanly.
        }

        _logger.LogInformation("ThrottleMonitorService stopped");
    }

    /// <summary>
    /// Process state transitions for all active sessions.
    /// Simplified model: FFmpeg is either actively transcoding or suspended (throttled).
    /// </summary>
    private Task ProcessStateTransitionsAsync()
    {
        foreach (var session in _transcodeService.GetAllSessions().ToList())
        {
            try
            {
                // Skip completed/dormant sessions
                if (session.State == TranscodeState.Completed || session.State == TranscodeState.Dormant)
                    continue;

                // Update latest segment from disk
                session.LatestSegmentIndex = _transcodeService.GetLatestSegmentIndex(session.SessionDirectory);

                // Calculate buffer
                int bufferSeconds = _transcodeService.CalculateBufferSeconds(session);

                // Check for process completion (not crash - just finished transcoding)
                if (session.Process != null && session.Process.HasExited && !session.IsSuspended)
                {
                    // FFmpeg exited on its own - likely finished the file
                    session.State = TranscodeState.Completed;
                    _logger.LogInformation("Session {MediaId} completed (FFmpeg exited normally)", session.Key.MediaId);
                    continue;
                }

                // Check for client inactivity (user navigated away or closed browser)
                var inactiveSeconds = (DateTime.UtcNow - session.LastClientRequestTime).TotalSeconds;
                if (inactiveSeconds > ClientInactivityTimeoutSeconds && 
                    bufferSeconds >= TranscodeService.ThrottleBufferMaxSeconds)
                {
                    _logger.LogInformation("Session {MediaId} entering DORMANT due to client inactivity ({Inactive}s)", 
                        session.Key.MediaId, (int)inactiveSeconds);
                    _transcodeService.EnterDormantState(session.Key);
                    continue;
                }

                // Handle pause request - fill buffer then enter dormant
                if (session.IsPaused && session.State != TranscodeState.Dormant)
                {
                    if (bufferSeconds >= TranscodeService.ThrottleBufferMaxSeconds)
                    {
                        _transcodeService.EnterDormantState(session.Key);
                        _logger.LogInformation("Session {MediaId} entered DORMANT (paused with full buffer)", session.Key.MediaId);
                        continue;
                    }
                    // Otherwise, keep transcoding to fill buffer before pausing
                }

                // SIMPLIFIED STATE MACHINE: Only 2 active states
                switch (session.State)
                {
                    case TranscodeState.Transcoding:
                        // FFmpeg is running - check if we should suspend
                        if (bufferSeconds >= TranscodeService.ThrottleBufferMaxSeconds)
                        {
                            // Buffer is full, suspend FFmpeg to save resources
                            _transcodeService.SuspendSession(session.Key);
                            _logger.LogInformation("Session {MediaId}: TRANSCODING → THROTTLED (buffer={Buffer}s)", 
                                session.Key.MediaId, bufferSeconds);
                        }
                        break;

                    case TranscodeState.Throttled:
                        // FFmpeg is suspended - check if we should resume
                        if (bufferSeconds <= TranscodeService.ThrottleBufferResumeSeconds)
                        {
                            // Buffer is running low, resume FFmpeg
                            _transcodeService.ResumeSession(session.Key);
                            _logger.LogInformation("Session {MediaId}: THROTTLED → TRANSCODING (buffer={Buffer}s)", 
                                session.Key.MediaId, bufferSeconds);
                        }
                        break;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing session {MediaId}", session.Key.MediaId);
            }
        }
        
        return Task.CompletedTask;
    }

    /// <summary>
    /// Handle FFmpeg crash/exit - mark session as completed and log
    /// </summary>
    private void HandleProcessExited(TranscodeSession session)
    {
        // With suspension-based throttling, if FFmpeg exits unexpectedly,
        // we just mark the session as needing attention - UI can show error
        session.State = TranscodeState.Completed;
        _logger.LogWarning("FFmpeg exited unexpectedly for {MediaId}, marking session complete", 
            session.Key.MediaId);
    }

    /// <summary>
    /// Check disk pressure and evict dormant sessions if needed
    /// </summary>
    private Task CheckDiskPressureAsync()
    {
        try
        {
            var tempDir = _transcodeService.GetTempDir();
            var driveInfo = new DriveInfo(Path.GetPathRoot(tempDir)!);
            var freeSpaceMB = driveInfo.AvailableFreeSpace / (1024 * 1024);

            if (freeSpaceMB < MinDiskSpaceThresholdMB)
            {
                _logger.LogWarning("Disk pressure detected: {FreeMB}MB free, threshold {ThresholdMB}MB", 
                    freeSpaceMB, MinDiskSpaceThresholdMB);

                // Get dormant sessions sorted by oldest first
                var dormantSessions = _transcodeService.GetAllSessions()
                    .Where(s => s.State == TranscodeState.Dormant)
                    .OrderBy(s => s.LastClientRequestTime)
                    .ToList();

                foreach (var session in dormantSessions)
                {
                    _transcodeService.DeleteDormantSession(session.Key);
                    _logger.LogInformation("Evicted dormant session {MediaId} due to disk pressure", session.Key.MediaId);

                    // Re-check disk space
                    freeSpaceMB = driveInfo.AvailableFreeSpace / (1024 * 1024);
                    if (freeSpaceMB >= MinDiskSpaceThresholdMB)
                        break;
                }

                if (freeSpaceMB < MinDiskSpaceThresholdMB)
                {
                    _logger.LogWarning("Disk still low after evicting all dormant sessions: {FreeMB}MB", freeSpaceMB);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error checking disk pressure");
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Clean up sessions dormant for more than 24 hours
    /// </summary>
    private void CleanupStaleSessions()
    {
        var threshold = DateTime.UtcNow.AddHours(-MaxDormantAgeHours);
        var staleSessions = _transcodeService.GetAllSessions()
            .Where(s => s.State == TranscodeState.Dormant && s.LastClientRequestTime < threshold)
            .ToList();

        foreach (var session in staleSessions)
        {
            _transcodeService.DeleteDormantSession(session.Key);
            _logger.LogInformation("Deleted stale dormant session {MediaId} (last activity: {LastActivity})", 
                session.Key.MediaId, session.LastClientRequestTime);
        }

        if (staleSessions.Any())
        {
            _logger.LogInformation("Cleaned up {Count} stale session(s)", staleSessions.Count);
        }
    }
}
