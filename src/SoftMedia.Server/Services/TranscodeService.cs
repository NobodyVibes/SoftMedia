using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using SoftMedia.Server.Services.Abstractions;

namespace SoftMedia.Server.Services;


/// <summary>
/// Key for tracking unique transcode sessions (mediaId + userId + subtitle track combination)
/// </summary>
public record TranscodeSessionKey(Guid MediaId, Guid UserId, int? SubtitleTrackIndex);

/// <summary>
/// Transcode state for throttling state machine.
/// Simplified model: FFmpeg is either actively transcoding or suspended.
/// </summary>
public enum TranscodeState
{
    /// <summary>FFmpeg is actively transcoding segments</summary>
    Transcoding,
    /// <summary>FFmpeg is suspended (paused) because buffer is full</summary>
    Throttled,
    /// <summary>User paused playback, FFmpeg stopped, segments retained</summary>
    Dormant,
    /// <summary>Session ended, cleanup complete</summary>
    Completed
}

/// <summary>
/// Represents an active transcode session with throttling state
/// </summary>
public class TranscodeSession
{
    public TranscodeSessionKey Key { get; init; } = null!;
    public Guid UserId { get; init; }
    public string InputPath { get; init; } = string.Empty;
    public Process? Process { get; set; }
    public TranscodeState State { get; set; } = TranscodeState.Transcoding;
    public bool IsSuspended { get; set; } = false;  // Tracks if FFmpeg process is currently suspended
    public double? SeekPosition { get; set; }  // Starting seek position for this session
    public int LatestSegmentIndex { get; set; } = 0;
    public int ClientSegmentIndex { get; set; } = 0;
    public DateTime LastClientRequestTime { get; set; } = DateTime.UtcNow;
    public DateTime SessionStartTime { get; init; } = DateTime.UtcNow;
    public bool IsPaused { get; set; } = false;
    public int CrashRetryCount { get; set; } = 0;
    public string SessionDirectory { get; init; } = string.Empty;
    
    /// <summary>
    /// Path to extracted WebVTT subtitle file for sidecar delivery (null if no subtitles selected)
    /// </summary>
    public string? SubtitleVttPath { get; set; }
    
    /// <summary>
    /// Language code of the selected subtitle track (for HLS manifest)
    /// </summary>
    public string? SubtitleLanguage { get; set; }
    
    /// <summary>
    /// True if the selected subtitle is bitmap-based (PGS, VOBSUB) and requires burn-in
    /// </summary>
    public bool IsBitmapSubtitle { get; set; } = false;
    
    /// <summary>
    /// Target resolution for transcoding (e.g., "720p", "1080p", "4k", "original")
    /// </summary>
    public string? TargetResolution { get; set; }
    
    /// <summary>
    /// Target video codec for transcoding (e.g., "h264", "hevc", "av1")
    /// </summary>
    public string? TargetCodec { get; set; }
    
    /// <summary>
    /// Whether to preserve HDR (skip tone mapping)
    /// </summary>
    public bool PreserveHdr { get; set; }
    
    /// <summary>
    /// Selected audio track index (null = use default audio track)
    /// </summary>
    public int? AudioTrackIndex { get; set; }

    /// <summary>
    /// Maximum bitrate limit in kbps (null = unlimited)
    /// </summary>
    public int? MaxBitrate { get; set; }
}

/// <summary>
/// Manages video transcoding sessions with throttling support.
/// Registered as Singleton to maintain process tracking across all HTTP requests.
/// </summary>
public class TranscodeService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<TranscodeService> _logger;
    private readonly IProcessController _processController;
    private readonly ConcurrentDictionary<TranscodeSessionKey, TranscodeSession> _activeSessions = new();
    private readonly ConcurrentDictionary<TranscodeSessionKey, SemaphoreSlim> _sessionLocks = new();
    private readonly string _tempDir;

    // Throttling thresholds (in seconds of buffer)
    public const int ThrottleBufferMaxSeconds = 120;     // Suspend FFmpeg when buffer exceeds this
    public const int ThrottleBufferResumeSeconds = 60;   // Resume FFmpeg when buffer drops below this
    public const int HlsSegmentDurationSeconds = 6;      // Target segment duration (actual varies)
    public const int MaxCrashRetries = 3;

    // Support both .ts (MPEG-TS) and .m4s (fMP4) segment extensions
    private static readonly Regex SegmentPattern = new(@"^seg_(\d+)\.(ts|m4s)$", RegexOptions.Compiled);

    public TranscodeService(
        IServiceScopeFactory scopeFactory, 
        ILogger<TranscodeService> logger, 
        IProcessController processController,
        IConfiguration config)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _processController = processController;
        _tempDir = Path.Combine(Directory.GetCurrentDirectory(), "transcode-temp");
        if (!Directory.Exists(_tempDir))
        {
            Directory.CreateDirectory(_tempDir);
        }
    }

    /// <summary>
    /// Get the session directory for a specific transcode session
    /// </summary>
    public string GetSessionDir(Guid mediaId, Guid userId, int? subtitleTrackIndex)
    {
        var suffix = subtitleTrackIndex.HasValue ? $"_sub{subtitleTrackIndex.Value}" : "";
        // Include userId to isolate transcode sessions per user
        return Path.Combine(_tempDir, $"{mediaId}_{userId}{suffix}");
    }

    /// <summary>
    /// Get the temp directory path
    /// </summary>
    public string GetTempDir() => _tempDir;

    /// <summary>
    /// Get all active sessions (for monitoring service)
    /// </summary>
    public IEnumerable<TranscodeSession> GetAllSessions() => _activeSessions.Values;

    /// <summary>
    /// Get a specific session by key
    /// </summary>
    public TranscodeSession? GetSession(TranscodeSessionKey key)
    {
        _activeSessions.TryGetValue(key, out var session);
        return session;
    }
    
    /// <summary>
    /// Get a specific session by media/user/subtitle combination
    /// </summary>
    public TranscodeSession? GetSession(Guid mediaId, Guid userId, int? subtitleTrackIndex)
    {
        var key = new TranscodeSessionKey(mediaId, userId, subtitleTrackIndex);
        return GetSession(key);
    }

    /// <summary>
    /// Extract segment index from filename like "seg_042.ts" or "seg_042.m4s"
    /// </summary>
    public static int ExtractSegmentIndex(string segmentName)
    {
        var match = SegmentPattern.Match(segmentName);
        return match.Success ? int.Parse(match.Groups[1].Value) : -1;
    }

    /// <summary>
    /// Get the latest segment index from disk
    /// </summary>
    public int GetLatestSegmentIndex(string sessionDir)
    {
        if (!Directory.Exists(sessionDir)) return 0;
        // Check for both .ts and .m4s segments (fMP4 uses .m4s)
        var tsFiles = Directory.GetFiles(sessionDir, "seg_*.ts");
        var m4sFiles = Directory.GetFiles(sessionDir, "seg_*.m4s");
        var files = tsFiles.Concat(m4sFiles).ToArray();
        return files
            .Select(f => ExtractSegmentIndex(Path.GetFileName(f)))
            .Where(i => i >= 0)
            .DefaultIfEmpty(0)
            .Max();
    }

    /// <summary>
    /// Parse the HLS playlist to get actual cumulative duration up to segmentCount segments.
    /// This is more accurate than assuming fixed 6s segments since actual durations vary (4-10s).
    /// </summary>
    public double GetActualPlaylistDuration(string sessionDir, int segmentCount)
    {
        var playlistPath = Path.Combine(sessionDir, "master.m3u8");
        if (!File.Exists(playlistPath))
        {
            // Fallback to fixed estimate
            _logger.LogWarning("Playlist not found, using estimated duration for {Count} segments", segmentCount);
            return segmentCount * HlsSegmentDurationSeconds;
        }

        try
        {
            var lines = File.ReadAllLines(playlistPath);
            double totalDuration = 0;
            int currentSegmentIndex = 0;
            
            foreach (var line in lines)
            {
                // Parse #EXTINF:duration, lines
                if (line.StartsWith("#EXTINF:"))
                {
                    var durationStr = line.Substring(8).Split(',')[0];
                    if (double.TryParse(durationStr, System.Globalization.NumberStyles.Float, 
                        System.Globalization.CultureInfo.InvariantCulture, out var duration))
                    {
                        // Only count segments up to the requested count
                        if (currentSegmentIndex < segmentCount)
                        {
                            totalDuration += duration;
                        }
                        currentSegmentIndex++;
                    }
                }
            }
            
            _logger.LogDebug("Parsed playlist: {Count} segments, total duration {Duration}s", 
                segmentCount, totalDuration);
            
            return totalDuration > 0 ? totalDuration : segmentCount * HlsSegmentDurationSeconds;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse playlist for duration, using estimate");
            return segmentCount * HlsSegmentDurationSeconds;
        }
    }

    /// <summary>
    /// Calculate buffer in seconds
    /// </summary>
    public int CalculateBufferSeconds(TranscodeSession session)
    {
        var bufferSegments = session.LatestSegmentIndex - session.ClientSegmentIndex;
        return Math.Max(0, bufferSegments) * HlsSegmentDurationSeconds;
    }

    /// <summary>
    /// Update client position when a segment is requested
    /// </summary>
    public void UpdateClientPosition(TranscodeSessionKey key, int segmentIndex)
    {
        if (_activeSessions.TryGetValue(key, out var session))
        {
            session.ClientSegmentIndex = segmentIndex;
            session.LastClientRequestTime = DateTime.UtcNow;
            session.CrashRetryCount = 0; // Reset on successful activity
            session.IsPaused = false;    // Client is actively requesting - wake from DORMANT if needed
            _logger.LogDebug("Client position updated: {MediaId} -> segment {Index}", key.MediaId, segmentIndex);
        }
    }

    /// <summary>
    /// Set paused state for a session
    /// </summary>
    public bool SetPaused(TranscodeSessionKey key, Guid userId, bool isPaused)
    {
        if (_activeSessions.TryGetValue(key, out var session))
        {
            // Validate ownership
            if (session.UserId != userId)
            {
                _logger.LogWarning("User {UserId} attempted to pause session owned by {OwnerId}", userId, session.UserId);
                return false;
            }
            
            session.IsPaused = isPaused;
            session.LastClientRequestTime = DateTime.UtcNow;
            _logger.LogInformation("Session {MediaId} paused={IsPaused}", key.MediaId, isPaused);
            return true;
        }
        return false;
    }

    /// <summary>
    /// Start transcoding with optional subtitle burn-in, seek position, resolution, and read rate.
    /// </summary>
    public async Task<TranscodeSession?> StartTranscodeAsync(
        Guid mediaId, 
        Guid userId,
        string inputPath, 
        int? subtitleTrackIndex = null, 
        double? seekPosition = null,
        string? resolution = null,
        double? readRate = null,
        string? codec = null,
        bool? preserveHdr = null,
        int? audioTrack = null,
        int? maxBitrate = null)
    {
        // Check concurrent transcode limit from settings
        using (var scope = _scopeFactory.CreateScope())
        {
            var settingsService = scope.ServiceProvider.GetRequiredService<ISettingsService>();
            var maxConcurrent = await settingsService.GetSettingAsync("MaxSimultaneousTranscodes", 0);
            
            if (maxConcurrent > 0)
            {
                var activeCount = _activeSessions.Count(s => s.Value.State != TranscodeState.Dormant && s.Value.State != TranscodeState.Completed);
                if (activeCount >= maxConcurrent)
                {
                    _logger.LogWarning("Max concurrent transcodes ({Max}) reached, rejecting new session for {MediaId}", 
                        maxConcurrent, mediaId);
                    return null; // Caller should handle HTTP 503
                }
            }
        }
        
        var sessionKey = new TranscodeSessionKey(mediaId, userId, subtitleTrackIndex);
        var sessionLock = _sessionLocks.GetOrAdd(sessionKey, _ => new SemaphoreSlim(1, 1));
        
        await sessionLock.WaitAsync();
        try
        {
            // Check if session already exists
            if (_activeSessions.TryGetValue(sessionKey, out var existingSession))
            {

                // Verify the session directory still exists (could have been cleaned up)
                if (Directory.Exists(existingSession.SessionDirectory))
                {
                    // Check if seek position changed - if so, restart the transcode
                    if (existingSession.SeekPosition != seekPosition)
                    {
                        _logger.LogInformation("Seek position changed from {OldSeek} to {NewSeek} for {MediaId}, restarting transcode",
                            existingSession.SeekPosition, seekPosition, mediaId);
                        await StopSessionInternalAsync(existingSession, sessionKey);
                    }
                    else
                    {
                        _logger.LogDebug("Transcode session already active for {MediaId}", mediaId);
                        
                        // Check if subtitles were requested but not extracted (e.g., session from before fix)
                        if (subtitleTrackIndex.HasValue && existingSession.SubtitleVttPath == null && !existingSession.IsBitmapSubtitle)
                        {
                            _logger.LogInformation("Existing session missing subtitles, checking codec for {MediaId}", mediaId);
                            using var scope = _scopeFactory.CreateScope();
                            var ffmpegService = scope.ServiceProvider.GetRequiredService<IFFmpegService>();
                            
                            // Check if this is a bitmap subtitle (needs burn-in, not sidecar)
                            var subtitleCodec = await ffmpegService.ProbeSubtitleCodecAsync(inputPath, subtitleTrackIndex.Value);
                            if (FFmpegService.IsBitmapSubtitleCodec(subtitleCodec))
                            {
                                _logger.LogWarning("Subtitle track {Index} is bitmap-based ({Codec}) - requires burn-in. " +
                                    "Restarting transcode with subtitle overlay.", subtitleTrackIndex.Value, subtitleCodec);
                                existingSession.IsBitmapSubtitle = true;
                                // Stop current session and restart with burn-in
                                await StopSessionInternalAsync(existingSession, sessionKey);
                                // Fall through to create a new session with burn-in
                            }
                            else
                            {
                                // Text subtitle - extract to WebVTT
                                var subtitleStreamIndex = await ffmpegService.GetSubtitleStreamIndexAsync(inputPath, subtitleTrackIndex.Value);
                                var vttPath = Path.Combine(existingSession.SessionDirectory, "subtitles.vtt");
                                
                                var extracted = await ffmpegService.ExtractSubtitleToVttAsync(inputPath, subtitleStreamIndex, vttPath);
                                if (extracted)
                                {
                                    existingSession.SubtitleVttPath = vttPath;
                                    if (existingSession.SeekPosition.HasValue && existingSession.SeekPosition.Value > 0)
                                    {
                                        ffmpegService.OffsetWebVttTimestamps(vttPath, existingSession.SeekPosition.Value);
                                    }
                                }
                                return existingSession;
                            }
                        }
                        else
                        {
                            return existingSession;
                        }
                        
                        return existingSession;
                    }
                }
                else
                {
                    // Session directory was cleaned up, remove stale session and restart
                    _logger.LogInformation("Session directory missing for {MediaId}, restarting transcode", mediaId);
                    _activeSessions.TryRemove(sessionKey, out _);
                }
            }

            var sessionDir = GetSessionDir(mediaId, userId, subtitleTrackIndex);
            
            // Clean up any existing session directory
            if (Directory.Exists(sessionDir))
            {
                try { Directory.Delete(sessionDir, true); }
                catch (Exception ex) { _logger.LogWarning(ex, "Could not clean up existing session dir: {Dir}", sessionDir); }
            }

            // Create session object
            var session = new TranscodeSession
            {
                Key = sessionKey,
                UserId = userId,
                InputPath = inputPath,
                State = TranscodeState.Transcoding,
                SeekPosition = seekPosition,  // Store seek position to detect changes
                TargetResolution = resolution ?? "original",  // Store resolution for FFmpeg
                TargetCodec = codec,  // Store codec from URL (may be null)
                PreserveHdr = preserveHdr ?? false,  // Store HDR preference
                AudioTrackIndex = audioTrack,  // Store selected audio track
                SessionDirectory = sessionDir,
                SessionStartTime = DateTime.UtcNow,
                LastClientRequestTime = DateTime.UtcNow,
                MaxBitrate = maxBitrate
            };

            _logger.LogInformation("Created new transcode session for {MediaId}: subtitleTrackIndex={Sub}, sessionDir={Dir}",
                mediaId, subtitleTrackIndex?.ToString() ?? "null", sessionDir);

            // Extract text subtitles to WebVTT for sidecar delivery (if subtitle selected)
            if (subtitleTrackIndex.HasValue)
            {
                using var scope = _scopeFactory.CreateScope();
                var ffmpegService = scope.ServiceProvider.GetRequiredService<IFFmpegService>();
                
                // Create session directory if it doesn't exist (needed before FFmpeg starts)
                Directory.CreateDirectory(sessionDir);
                
                // Check if subtitle is bitmap-based (PGS, VOBSUB) - these need burn-in
                var subtitleCodec = await ffmpegService.ProbeSubtitleCodecAsync(inputPath, subtitleTrackIndex.Value);
                
                // Probe subtitle language from FFprobe tags
                session.SubtitleLanguage = await ffmpegService.ProbeSubtitleLanguageAsync(inputPath, subtitleTrackIndex.Value);
                if (session.SubtitleLanguage != null)
                {
                    _logger.LogInformation("Detected subtitle language: {Lang}", session.SubtitleLanguage);
                }
                
                if (FFmpegService.IsBitmapSubtitleCodec(subtitleCodec))
                {
                    _logger.LogInformation("Subtitle track {Index} is bitmap-based ({Codec}) - will use burn-in overlay.", 
                        subtitleTrackIndex.Value, subtitleCodec);
                    session.IsBitmapSubtitle = true;
                    // Burn-in will be handled by StartFFmpegProcessAsync
                }
                else
                {
                    // Get the subtitle stream index within subtitle streams (0-based for -map 0:s:N)
                    var subtitleStreamIndex = await ffmpegService.GetSubtitleStreamIndexAsync(inputPath, subtitleTrackIndex.Value);
                    var vttPath = Path.Combine(sessionDir, "subtitles.vtt");
                    
                    _logger.LogInformation("Extracting subtitle track {Index} (stream {Stream}, codec={Codec}) to WebVTT", 
                        subtitleTrackIndex.Value, subtitleStreamIndex, subtitleCodec ?? "unknown");
                    
                    var extracted = await ffmpegService.ExtractSubtitleToVttAsync(inputPath, subtitleStreamIndex, vttPath);
                    if (extracted)
                    {
                        session.SubtitleVttPath = vttPath;
                        _logger.LogInformation("Subtitle extracted successfully for session {MediaId}", mediaId);
                        
                        // Offset timestamps if seeking - VTT has absolute times but HLS plays from 0
                        if (seekPosition.HasValue && seekPosition.Value > 0)
                        {
                            ffmpegService.OffsetWebVttTimestamps(vttPath, seekPosition.Value);
                        }
                    }
                    else
                    {
                        _logger.LogWarning("Failed to extract subtitle - will transcode without subtitles");
                    }
                }
            }

            // Start FFmpeg (without subtitle burn-in since we're using sidecar)
            var process = await StartFFmpegProcessAsync(session, seekPosition);
            if (process == null)
            {
                _logger.LogError("Failed to start FFmpeg for {MediaId}", mediaId);
                return null;
            }

            session.Process = process;
            _activeSessions.TryAdd(sessionKey, session);
            
            _logger.LogInformation("Session {MediaId} added to active sessions. SubtitleVttPath={Path}",
                mediaId, session.SubtitleVttPath ?? "null");
            
            // Wait for playlist to be created
            await Task.Delay(3000);
            
            return session;
        }
        finally
        {
            sessionLock.Release();
        }
    }

    /// <summary>
    /// Suspend the FFmpeg process for a session (throttle when buffer is full).
    /// Does NOT kill the process - uses OS-level suspension so it can resume exactly where it left off.
    /// </summary>
    public bool SuspendSession(TranscodeSessionKey key)
    {
        if (!_activeSessions.TryGetValue(key, out var session))
        {
            return false;
        }

        if (session.Process == null || session.Process.HasExited)
        {
            _logger.LogDebug("Cannot suspend session {MediaId}: process is null or exited", key.MediaId);
            return false;
        }

        if (session.IsSuspended)
        {
            _logger.LogDebug("Session {MediaId} is already suspended", key.MediaId);
            return true;
        }

        var success = _processController.Suspend(session.Process);
        if (success)
        {
            session.IsSuspended = true;
            session.State = TranscodeState.Throttled;
            _logger.LogInformation("Suspended FFmpeg for {MediaId} (buffer full)", key.MediaId);
        }
        else
        {
            _logger.LogWarning("Failed to suspend FFmpeg for {MediaId}", key.MediaId);
        }
        
        return success;
    }

    /// <summary>
    /// Resume a suspended FFmpeg process (when buffer runs low).
    /// The process continues exactly where it left off - no segment overlap.
    /// </summary>
    public bool ResumeSession(TranscodeSessionKey key)
    {
        if (!_activeSessions.TryGetValue(key, out var session))
        {
            return false;
        }

        if (session.Process == null || session.Process.HasExited)
        {
            _logger.LogDebug("Cannot resume session {MediaId}: process is null or exited", key.MediaId);
            return false;
        }

        if (!session.IsSuspended)
        {
            _logger.LogDebug("Session {MediaId} is not suspended", key.MediaId);
            return true;
        }

        var success = _processController.Resume(session.Process);
        if (success)
        {
            session.IsSuspended = false;
            session.State = TranscodeState.Transcoding;
            _logger.LogInformation("Resumed FFmpeg for {MediaId} (buffer low)", key.MediaId);
        }
        else
        {
            _logger.LogWarning("Failed to resume FFmpeg for {MediaId}", key.MediaId);
        }
        
        return success;
    }


    /// <summary>
    /// Start FFmpeg process with current session settings
    /// </summary>
    private async Task<Process?> StartFFmpegProcessAsync(TranscodeSession session, double? seekPosition)
    {
        using var scope = _scopeFactory.CreateScope();
        var ffmpegService = scope.ServiceProvider.GetRequiredService<IFFmpegService>();

        // For text subtitles, we use sidecar WebVTT - no burn-in needed
        // For bitmap subtitles (PGS), we need to burn them into the video stream
        int? subtitleBurnInIndex = null;
        if (session.IsBitmapSubtitle && session.Key.SubtitleTrackIndex.HasValue)
        {
            subtitleBurnInIndex = session.Key.SubtitleTrackIndex.Value;
            _logger.LogInformation("Using burn-in for bitmap subtitle track {Index}", subtitleBurnInIndex);
        }
        
        var startInfo = await ffmpegService.GetTranscodeArgumentsAsync(
            session.InputPath, 
            session.SessionDirectory, 
            "seg", 
            subtitleBurnInIndex,  // Pass subtitle for burn-in if bitmap, null otherwise (sidecar WebVTT)
            seekPosition,
            null,  // No read rate - FFmpeg runs at full speed, throttled via suspend/resume
            session.TargetResolution,  // Pass resolution from session
            session.TargetCodec,       // Pass codec from session
            session.PreserveHdr,       // Pass HDR preference from session
            session.AudioTrackIndex,   // Pass audio track from session
            session.MaxBitrate);       // Pass max bitrate from session

        _logger.LogInformation("Starting FFmpeg for {MediaId} (seek={Seek}): {Args}", 
            session.Key.MediaId, seekPosition, startInfo.Arguments);

        var process = new Process { StartInfo = startInfo };
        
        process.ErrorDataReceived += (sender, e) => 
        {
            if (!string.IsNullOrEmpty(e.Data))
            {
                _logger.LogDebug("FFmpeg [{MediaId}]: {Data}", session.Key.MediaId, e.Data);
            }
        };

        if (process.Start())
        {
            process.BeginErrorReadLine();
            return process;
        }

        return null;
    }

    /// <summary>
    /// Get the HLS playlist for a transcode session.
    /// </summary>
    public Stream? GetPlaylist(Guid mediaId, Guid userId, int? subtitleTrackIndex = null)
    {
        var sessionDir = GetSessionDir(mediaId, userId, subtitleTrackIndex);
        var playlistPath = Path.Combine(sessionDir, "master.m3u8");
        if (File.Exists(playlistPath))
        {
            return new FileStream(playlistPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        }
        return null;
    }

    /// <summary>
    /// Get a segment file from a transcode session.
    /// </summary>
    public Stream? GetSegment(Guid mediaId, Guid userId, string segmentName, int? subtitleTrackIndex = null)
    {
        // Validate segment name pattern (security)
        if (!SegmentPattern.IsMatch(segmentName))
        {
            _logger.LogWarning("Invalid segment name rejected: {Name}", segmentName);
            return null;
        }

        var sessionDir = GetSessionDir(mediaId, userId, subtitleTrackIndex);
        var segmentPath = Path.Combine(sessionDir, segmentName);
        if (File.Exists(segmentPath))
        {
            return new FileStream(segmentPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        }
        return null;
    }

    /// <summary>
    /// Get the fMP4 initialization segment (init.mp4) for a transcode session.
    /// This is required for fMP4/CMAF HLS playback.
    /// </summary>
    public Stream? GetInitSegment(Guid mediaId, Guid userId, int? subtitleTrackIndex = null)
    {
        var sessionDir = GetSessionDir(mediaId, userId, subtitleTrackIndex);
        var initPath = Path.Combine(sessionDir, "init.mp4");
        if (File.Exists(initPath))
        {
            return new FileStream(initPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        }
        _logger.LogWarning("Init segment not found at {Path}", initPath);
        return null;
    }

    /// <summary>
    /// Stop a specific transcode session and clean up.
    /// </summary>
    public void StopTranscode(Guid mediaId, Guid userId, int? subtitleTrackIndex = null, bool deleteFiles = true)
    {
        var sessionKey = new TranscodeSessionKey(mediaId, userId, subtitleTrackIndex);
        
        if (_activeSessions.TryRemove(sessionKey, out var session))
        {
            StopSession(session, deleteFiles);
        }
    }

    /// <summary>
    /// Stop all transcode sessions for a given media item and user.
    /// </summary>
    public void StopAllTranscodesForUser(Guid mediaId, Guid userId)
    {
        var keysToRemove = _activeSessions.Keys.Where(k => k.MediaId == mediaId && k.UserId == userId).ToList();
        foreach (var key in keysToRemove)
        {
            if (_activeSessions.TryRemove(key, out var session))
            {
                StopSession(session, deleteFiles: true);
            }
        }
    }

    /// <summary>
    /// Remove session from tracking without killing process (for DORMANT state)
    /// </summary>
    public void EnterDormantState(TranscodeSessionKey key)
    {
        if (_activeSessions.TryGetValue(key, out var session))
        {
            // Stop process but keep files
            if (session.Process != null && !session.Process.HasExited)
            {
                try
                {
                    session.Process.Kill();
                    session.Process.Dispose();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error stopping FFmpeg for dormant state");
                }
            }
            session.Process = null;
            session.State = TranscodeState.Dormant;
            session.IsPaused = true; // Mark as paused so we only wake on new segment requests
            _logger.LogInformation("Session {MediaId} entered DORMANT state", key.MediaId);
        }
    }

    /// <summary>
    /// Delete a dormant session and its files
    /// </summary>
    public void DeleteDormantSession(TranscodeSessionKey key)
    {
        if (_activeSessions.TryRemove(key, out var session))
        {
            StopSession(session, deleteFiles: true);
            _logger.LogInformation("Dormant session {MediaId} deleted", key.MediaId);
        }
    }

    /// <summary>
    /// Internal helper to stop a session and remove it from active sessions.
    /// Used when restarting a session with different parameters.
    /// </summary>
    private async Task StopSessionInternalAsync(TranscodeSession session, TranscodeSessionKey sessionKey)
    {
        StopSession(session, deleteFiles: true);
        _activeSessions.TryRemove(sessionKey, out _);
        await Task.Delay(100); // Brief delay for filesystem to settle
    }

    private void StopSession(TranscodeSession session, bool deleteFiles)
    {
        try
        {
            if (session.Process != null && !session.Process.HasExited)
            {
                try 
                {
                    session.Process.Kill();
                    // Give the process time to release handles
                    session.Process.WaitForExit(2000); 
                }
                catch (Exception ex)
                {
                     _logger.LogWarning("Error killing process for session {Key}: {Message}", session.Key, ex.Message);
                }
            }
            session.Process?.Dispose();
            
            if (deleteFiles && Directory.Exists(session.SessionDirectory))
            {
                // Retry deletion strategy to handle transient file locks
                int attempts = 0;
                while (attempts < 3)
                {
                    try
                    {
                        Directory.Delete(session.SessionDirectory, true);
                        break; 
                    }
                    catch (IOException)
                    {
                        attempts++;
                        if (attempts >= 3) throw;
                        Thread.Sleep(200); 
                    }
                }
            }
            
            session.State = TranscodeState.Completed;
        }
        catch (Exception ex)
        {
            // Downgrade IO errors to warnings to avoid "Failure" log spam for benign locking issues
            if (ex is IOException)
            {
                _logger.LogWarning("Cleanup warning for {Key}: {Message}", session.Key, ex.Message);
            }
            else
            {
                _logger.LogError(ex, "Error stopping transcode session for {Key}", session.Key);
            }
        }
    }
}
