using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;

namespace SoftMedia.Server.Services;

/// <summary>
/// Key for tracking unique transcode sessions (mediaId + subtitle track combination)
/// </summary>
public record TranscodeSessionKey(Guid MediaId, int? SubtitleTrackIndex);

/// <summary>
/// Transcode state for throttling state machine
/// </summary>
public enum TranscodeState
{
    /// <summary>Full speed transcoding at start</summary>
    Burst,
    /// <summary>2.0x speed to build buffer</summary>
    Catching,
    /// <summary>1.0x speed steady state</summary>
    Cruising,
    /// <summary>Paused, FFmpeg stopped, segments retained</summary>
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
    public TranscodeState State { get; set; } = TranscodeState.Burst;
    public double? CurrentReadRate { get; set; } = null;  // null = BURST (full speed)
    public int LatestSegmentIndex { get; set; } = 0;
    public int ClientSegmentIndex { get; set; } = 0;
    public DateTime LastClientRequestTime { get; set; } = DateTime.UtcNow;
    public DateTime SessionStartTime { get; init; } = DateTime.UtcNow;
    public bool IsPaused { get; set; } = false;
    public int CrashRetryCount { get; set; } = 0;
    public string SessionDirectory { get; init; } = string.Empty;
}

/// <summary>
/// Manages video transcoding sessions with throttling support.
/// Registered as Singleton to maintain process tracking across all HTTP requests.
/// </summary>
public class TranscodeService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<TranscodeService> _logger;
    private readonly ConcurrentDictionary<TranscodeSessionKey, TranscodeSession> _activeSessions = new();
    private readonly ConcurrentDictionary<TranscodeSessionKey, SemaphoreSlim> _sessionLocks = new();
    private readonly string _tempDir;

    // Throttling constants (from plan v1.5)
    public const int BurstThresholdSeconds = 30;
    public const int ThrottleThresholdSeconds = 120;
    public const int ResumeBoostThresholdSeconds = 90;
    public const int HlsSegmentDurationSeconds = 6;
    public const int MaxCrashRetries = 3;

    private static readonly Regex SegmentPattern = new(@"^seg_(\d+)\.ts$", RegexOptions.Compiled);

    public TranscodeService(IServiceScopeFactory scopeFactory, ILogger<TranscodeService> logger, IConfiguration config)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _tempDir = Path.Combine(Directory.GetCurrentDirectory(), "transcode-temp");
        if (!Directory.Exists(_tempDir))
        {
            Directory.CreateDirectory(_tempDir);
        }
    }

    /// <summary>
    /// Get the session directory for a specific transcode session
    /// </summary>
    public string GetSessionDir(Guid mediaId, int? subtitleTrackIndex)
    {
        var suffix = subtitleTrackIndex.HasValue ? $"_sub{subtitleTrackIndex.Value}" : "";
        return Path.Combine(_tempDir, $"{mediaId}{suffix}");
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
    /// Extract segment index from filename like "seg_042.ts"
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
        var files = Directory.GetFiles(sessionDir, "seg_*.ts");
        return files
            .Select(f => ExtractSegmentIndex(Path.GetFileName(f)))
            .Where(i => i >= 0)
            .DefaultIfEmpty(0)
            .Max();
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
    /// Start transcoding with optional subtitle burn-in, seek position, and read rate.
    /// </summary>
    public async Task<TranscodeSession?> StartTranscodeAsync(
        Guid mediaId, 
        Guid userId,
        string inputPath, 
        int? subtitleTrackIndex = null, 
        double? seekPosition = null,
        double? readRate = null)
    {
        var sessionKey = new TranscodeSessionKey(mediaId, subtitleTrackIndex);
        var sessionLock = _sessionLocks.GetOrAdd(sessionKey, _ => new SemaphoreSlim(1, 1));
        
        await sessionLock.WaitAsync();
        try
        {
            // Check if session already exists
            if (_activeSessions.TryGetValue(sessionKey, out var existingSession))
            {
                _logger.LogDebug("Transcode session already active for {MediaId}", mediaId);
                return existingSession;
            }

            var sessionDir = GetSessionDir(mediaId, subtitleTrackIndex);
            
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
                State = TranscodeState.Burst,
                CurrentReadRate = readRate,
                SessionDirectory = sessionDir,
                SessionStartTime = DateTime.UtcNow,
                LastClientRequestTime = DateTime.UtcNow
            };

            // Start FFmpeg
            var process = await StartFFmpegProcessAsync(session, seekPosition);
            if (process == null)
            {
                _logger.LogError("Failed to start FFmpeg for {MediaId}", mediaId);
                return null;
            }

            session.Process = process;
            _activeSessions.TryAdd(sessionKey, session);
            
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
    /// Restart FFmpeg with a new read rate (for state transitions)
    /// </summary>
    public async Task<bool> RestartWithReadRateAsync(TranscodeSessionKey key, double? newReadRate, TranscodeState newState)
    {
        if (!_activeSessions.TryGetValue(key, out var session))
        {
            return false;
        }

        var sessionLock = _sessionLocks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await sessionLock.WaitAsync();
        try
        {
            // Stop current process
            if (session.Process != null && !session.Process.HasExited)
            {
                try
                {
                    session.Process.Kill();
                    session.Process.Dispose();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error stopping FFmpeg during restart");
                }
            }

            // Update session state
            session.CurrentReadRate = newReadRate;
            session.State = newState;

            // Calculate seek position from latest segment
            double seekSeconds = session.LatestSegmentIndex * HlsSegmentDurationSeconds;

            // Start new process
            var process = await StartFFmpegProcessAsync(session, seekSeconds);
            if (process == null)
            {
                _logger.LogError("Failed to restart FFmpeg for {MediaId}", key.MediaId);
                return false;
            }

            session.Process = process;
            _logger.LogInformation("FFmpeg restarted for {MediaId}: state={State}, readRate={Rate}", 
                key.MediaId, newState, newReadRate?.ToString() ?? "BURST");
            
            return true;
        }
        finally
        {
            sessionLock.Release();
        }
    }

    /// <summary>
    /// Start FFmpeg process with current session settings
    /// </summary>
    private async Task<Process?> StartFFmpegProcessAsync(TranscodeSession session, double? seekPosition)
    {
        using var scope = _scopeFactory.CreateScope();
        var ffmpegService = scope.ServiceProvider.GetRequiredService<IFFmpegService>();

        var startInfo = ffmpegService.GetTranscodeArguments(
            session.InputPath, 
            session.SessionDirectory, 
            "seg", 
            session.Key.SubtitleTrackIndex, 
            seekPosition,
            session.CurrentReadRate);

        _logger.LogInformation("Starting FFmpeg for {MediaId} (readRate={Rate}, seek={Seek}): {Args}", 
            session.Key.MediaId, session.CurrentReadRate, seekPosition, startInfo.Arguments);

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
    public Stream? GetPlaylist(Guid mediaId, int? subtitleTrackIndex = null)
    {
        var sessionDir = GetSessionDir(mediaId, subtitleTrackIndex);
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
    public Stream? GetSegment(Guid mediaId, string segmentName, int? subtitleTrackIndex = null)
    {
        // Validate segment name pattern (security)
        if (!SegmentPattern.IsMatch(segmentName))
        {
            _logger.LogWarning("Invalid segment name rejected: {Name}", segmentName);
            return null;
        }

        var sessionDir = GetSessionDir(mediaId, subtitleTrackIndex);
        var segmentPath = Path.Combine(sessionDir, segmentName);
        if (File.Exists(segmentPath))
        {
            return new FileStream(segmentPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        }
        return null;
    }

    /// <summary>
    /// Stop a specific transcode session and clean up.
    /// </summary>
    public void StopTranscode(Guid mediaId, int? subtitleTrackIndex = null, bool deleteFiles = true)
    {
        var sessionKey = new TranscodeSessionKey(mediaId, subtitleTrackIndex);
        
        if (_activeSessions.TryRemove(sessionKey, out var session))
        {
            StopSession(session, deleteFiles);
        }
    }

    /// <summary>
    /// Stop all transcode sessions for a given media item.
    /// </summary>
    public void StopAllTranscodesForMedia(Guid mediaId)
    {
        var keysToRemove = _activeSessions.Keys.Where(k => k.MediaId == mediaId).ToList();
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

    private void StopSession(TranscodeSession session, bool deleteFiles)
    {
        try
        {
            if (session.Process != null && !session.Process.HasExited)
            {
                session.Process.Kill();
            }
            session.Process?.Dispose();
            
            if (deleteFiles && Directory.Exists(session.SessionDirectory))
            {
                Directory.Delete(session.SessionDirectory, true);
            }
            
            session.State = TranscodeState.Completed;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error stopping transcode session");
        }
    }
}
