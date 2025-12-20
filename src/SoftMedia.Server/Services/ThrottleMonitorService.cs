using System.Collections.Concurrent;

namespace SoftMedia.Server.Services;

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

        _logger.LogInformation("ThrottleMonitorService stopped");
    }

    /// <summary>
    /// Process state transitions for all active sessions
    /// </summary>
    private async Task ProcessStateTransitionsAsync()
    {
        foreach (var session in _transcodeService.GetAllSessions().ToList())
        {
            try
            {
                // Skip completed/dormant sessions
                if (session.State == TranscodeState.Completed)
                    continue;

                // Update latest segment from disk
                session.LatestSegmentIndex = _transcodeService.GetLatestSegmentIndex(session.SessionDirectory);

                // Calculate buffer
                int bufferSeconds = _transcodeService.CalculateBufferSeconds(session);

                // Check for crash (process exited unexpectedly)
                if (session.State != TranscodeState.Dormant && 
                    session.Process != null && 
                    session.Process.HasExited)
                {
                    await HandleCrashAsync(session);
                    continue;
                }

                // Check for client inactivity (no segment requests = user navigated away or closed browser)
                // Only enter DORMANT if:
                // 1. No segment requests for ClientInactivityTimeoutSeconds
                // 2. We have enough buffer so user can still watch (if they're still there)
                var inactiveSeconds = (DateTime.UtcNow - session.LastClientRequestTime).TotalSeconds;
                if (session.State != TranscodeState.Dormant && 
                    inactiveSeconds > ClientInactivityTimeoutSeconds &&
                    bufferSeconds >= TranscodeService.ThrottleThresholdSeconds)
                {
                    _logger.LogInformation("Session {MediaId} entering DORMANT due to client inactivity ({Inactive}s, buffer={Buffer}s)", 
                        session.Key.MediaId, (int)inactiveSeconds, bufferSeconds);
                    _transcodeService.EnterDormantState(session.Key);
                    continue;
                }

                // Handle pause request with low buffer (continue until buffer is full)
                if (session.IsPaused && session.State != TranscodeState.Dormant)
                {
                    if (bufferSeconds >= TranscodeService.ThrottleThresholdSeconds)
                    {
                        // Buffer is full, enter DORMANT
                        _transcodeService.EnterDormantState(session.Key);
                        _logger.LogInformation("Session {MediaId} entered DORMANT (paused with full buffer)", session.Key.MediaId);
                        continue;
                    }
                    // Otherwise, continue transcoding to fill buffer
                }

                // State machine transitions
                switch (session.State)
                {
                    case TranscodeState.Burst:
                        if (bufferSeconds >= TranscodeService.BurstThresholdSeconds)
                        {
                            // Transition to CATCHING (2.0x)
                            await _transcodeService.RestartWithReadRateAsync(session.Key, 2.0, TranscodeState.Catching);
                            _logger.LogInformation("Session {MediaId}: BURST → CATCHING (buffer={Buffer}s)", 
                                session.Key.MediaId, bufferSeconds);
                        }
                        break;

                    case TranscodeState.Catching:
                        if (bufferSeconds >= TranscodeService.ThrottleThresholdSeconds)
                        {
                            // Transition to CRUISING (1.0x)
                            await _transcodeService.RestartWithReadRateAsync(session.Key, 1.0, TranscodeState.Cruising);
                            _logger.LogInformation("Session {MediaId}: CATCHING → CRUISING (buffer={Buffer}s)", 
                                session.Key.MediaId, bufferSeconds);
                        }
                        else if (bufferSeconds < TranscodeService.BurstThresholdSeconds)
                        {
                            // Buffer critically low, boost to BURST (max speed)
                            await _transcodeService.RestartWithReadRateAsync(session.Key, null, TranscodeState.Burst);
                            _logger.LogInformation("Session {MediaId}: CATCHING → BURST (buffer critically low={Buffer}s)", 
                                session.Key.MediaId, bufferSeconds);
                        }
                        break;

                    case TranscodeState.Cruising:
                        // If no process is running, we're using existing buffer
                        // Only restart FFmpeg when buffer actually runs low
                        if (session.Process == null || session.Process.HasExited)
                        {
                            if (bufferSeconds < TranscodeService.BurstThresholdSeconds)
                            {
                                // Buffer critically low, need to restart at max speed
                                await _transcodeService.RestartWithReadRateAsync(session.Key, null, TranscodeState.Burst);
                                _logger.LogInformation("Session {MediaId}: CRUISING (no process) → BURST (buffer={Buffer}s)", 
                                    session.Key.MediaId, bufferSeconds);
                            }
                            else if (bufferSeconds < TranscodeService.ResumeBoostThresholdSeconds)
                            {
                                // Buffer getting low, restart at 2x
                                await _transcodeService.RestartWithReadRateAsync(session.Key, 2.0, TranscodeState.Catching);
                                _logger.LogInformation("Session {MediaId}: CRUISING (no process) → CATCHING (buffer={Buffer}s)", 
                                    session.Key.MediaId, bufferSeconds);
                            }
                            // else: buffer is fine, keep using existing segments
                        }
                        else
                        {
                            // FFmpeg is running, normal state transitions
                            if (bufferSeconds < TranscodeService.BurstThresholdSeconds)
                            {
                                await _transcodeService.RestartWithReadRateAsync(session.Key, null, TranscodeState.Burst);
                                _logger.LogInformation("Session {MediaId}: CRUISING → BURST (buffer critically low={Buffer}s)", 
                                    session.Key.MediaId, bufferSeconds);
                            }
                            else if (bufferSeconds < TranscodeService.ResumeBoostThresholdSeconds)
                            {
                                await _transcodeService.RestartWithReadRateAsync(session.Key, 2.0, TranscodeState.Catching);
                                _logger.LogInformation("Session {MediaId}: CRUISING → CATCHING (buffer dropped to {Buffer}s)", 
                                    session.Key.MediaId, bufferSeconds);
                            }
                        }
                        break;

                    case TranscodeState.Dormant:
                        // Check if user resumed
                        if (!session.IsPaused)
                        {
                            // Determine target state based on buffer
                            if (bufferSeconds >= TranscodeService.ThrottleThresholdSeconds)
                            {
                                // Buffer is full - don't restart FFmpeg, just change state
                                // Let existing segments serve playback until buffer drops
                                session.State = TranscodeState.Cruising;
                                _logger.LogInformation("Session {MediaId}: DORMANT → CRUISING (using existing buffer={Buffer}s, no FFmpeg restart)", 
                                    session.Key.MediaId, bufferSeconds);
                            }
                            else if (bufferSeconds < TranscodeService.BurstThresholdSeconds)
                            {
                                // Critically low buffer, go to BURST
                                await _transcodeService.RestartWithReadRateAsync(session.Key, null, TranscodeState.Burst);
                                _logger.LogInformation("Session {MediaId}: DORMANT → BURST (buffer critically low={Buffer}s)", 
                                    session.Key.MediaId, bufferSeconds);
                            }
                            else
                            {
                                // Moderate buffer - restart at 2x to build up
                                await _transcodeService.RestartWithReadRateAsync(session.Key, 2.0, TranscodeState.Catching);
                                _logger.LogInformation("Session {MediaId}: DORMANT → CATCHING (buffer={Buffer}s)", 
                                    session.Key.MediaId, bufferSeconds);
                            }
                        }
                        break;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing session {MediaId}", session.Key.MediaId);
            }
        }
    }

    /// <summary>
    /// Handle FFmpeg crash with retry logic
    /// </summary>
    private async Task HandleCrashAsync(TranscodeSession session)
    {
        session.CrashRetryCount++;
        _logger.LogWarning("FFmpeg crashed for {MediaId}, attempt {Count}/{Max}", 
            session.Key.MediaId, session.CrashRetryCount, TranscodeService.MaxCrashRetries);

        if (session.CrashRetryCount >= TranscodeService.MaxCrashRetries)
        {
            _logger.LogError("Max crash retries exceeded for {MediaId}, cleaning up", session.Key.MediaId);
            _transcodeService.StopTranscode(session.Key.MediaId, session.Key.UserId, session.Key.SubtitleTrackIndex, deleteFiles: true);
            return;
        }

        // Restart from last segment
        double seekSeconds = session.LatestSegmentIndex * TranscodeService.HlsSegmentDurationSeconds;
        await _transcodeService.RestartWithReadRateAsync(session.Key, session.CurrentReadRate, session.State);
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
