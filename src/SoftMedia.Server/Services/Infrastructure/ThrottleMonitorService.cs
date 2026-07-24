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

    // Throttling thresholds (from plan)
    private const int MinDiskSpaceThresholdMB = 500;
    private const int ClientInactivityTimeoutSeconds = 90;  // Stop FFmpeg if no segment requests for 90s (accounts for HLS buffering)

    private DateTime _lastDiskCheck = DateTime.MinValue;

    /// <summary>SR-WI-028: while disk space is below the threshold, live sessions are
    /// suspended and the buffer-low resume path is gated off, so ffmpeg can't write the
    /// disk to zero and die (which then looked like a silent stall to the client).</summary>
    private volatile bool _diskPressureActive;

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

                    // Age-based retention of dormant sessions and their on-disk segments
                    // is owned by TranscodeSegmentCleanupService (hourly, honouring the
                    // Transcoding.SegmentRetentionHours setting) — not here.
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
    private async Task ProcessStateTransitionsAsync()
    {
        foreach (var session in _transcodeService.GetAllSessions().ToList())
        {
            try
            {
                // Skip completed/dormant/failed sessions
                if (session.State is TranscodeState.Completed or TranscodeState.Dormant or TranscodeState.Failed)
                    continue;

                // Update latest segment from disk
                session.LatestSegmentIndex = _transcodeService.GetLatestSegmentIndex(session.SessionDirectory);

                // Calculate buffer
                int bufferSeconds = _transcodeService.CalculateBufferSeconds(session);

                // SR-WI-020: the retry budget refills only once transcoding has progressed
                // meaningfully past the last crash point — not on mere client activity.
                if (session.LastCrashSegmentIndex >= 0
                    && session.LatestSegmentIndex > session.LastCrashSegmentIndex + 2)
                {
                    session.CrashRetryCount = 0;
                    session.LastCrashSegmentIndex = -1;
                }

                // SR-WI-020: distinguish normal completion from a crash. The old code
                // labeled EVERY exit "completed (FFmpeg exited normally)", so a crashed
                // stream became a frozen playlist the client buffered against forever.
                if (session.Process != null && session.Process.HasExited && !session.IsSuspended)
                {
                    int exitCode;
                    try { exitCode = session.Process.ExitCode; }
                    catch { exitCode = -1; }

                    var playlistInfo = _transcodeService.GetPlaylistInfo(session.SessionDirectory);
                    if (exitCode == 0 || playlistInfo is { HasEndList: true })
                    {
                        session.State = TranscodeState.Completed;
                        _logger.LogInformation("Session {MediaId} completed (FFmpeg exited normally)", session.Key.MediaId);
                    }
                    else
                    {
                        _logger.LogWarning(
                            "FFmpeg crashed for {MediaId} (exit {Code}) at segment {Seg}; attempting revival (retry {Retry}/{Max})",
                            session.Key.MediaId, exitCode, session.LatestSegmentIndex,
                            session.CrashRetryCount + 1, TranscodeService.MaxCrashRetries);
                        await _transcodeService.TryReviveSessionAsync(session.Key, countAsCrashRetry: true);
                    }
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
                        // FFmpeg is suspended - check if we should resume. SR-WI-028: never
                        // resume under disk pressure — a resumed writer would race the
                        // eviction sweep straight back to a full disk.
                        if (bufferSeconds <= TranscodeService.ThrottleBufferResumeSeconds && !_diskPressureActive)
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

            if (freeSpaceMB >= MinDiskSpaceThresholdMB)
            {
                if (_diskPressureActive)
                {
                    _diskPressureActive = false;
                    _logger.LogInformation("Disk pressure cleared ({FreeMB}MB free); live transcodes may resume", freeSpaceMB);
                }
                return Task.CompletedTask;
            }

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
                    // SR-WI-028: dormant eviction wasn't enough — suspend LIVE writers too.
                    // Previously ffmpeg kept writing to zero free space and died, which then
                    // presented as a silent stall. Suspended sessions stay parked until the
                    // 30s cycle observes free space again (_diskPressureActive gates resume).
                    _diskPressureActive = true;
                    foreach (var live in _transcodeService.GetAllSessions()
                                 .Where(s => s.State == TranscodeState.Transcoding).ToList())
                    {
                        _transcodeService.SuspendSession(live.Key);
                        _logger.LogWarning("Suspended live session {MediaId} due to disk pressure", live.Key.MediaId);
                    }
                    _logger.LogWarning("Disk still low after evicting all dormant sessions: {FreeMB}MB — live transcodes suspended", freeSpaceMB);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error checking disk pressure");
        }

        return Task.CompletedTask;
    }

}
