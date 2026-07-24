using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using SoftMedia.Server.Services.Abstractions;
using SoftMedia.Server.Services.Transcoding.Models;

namespace SoftMedia.Server.Services.Transcoding;

/// <summary>
/// Thrown when a transcode cannot start because a concurrency cap (global or
/// per-user) is reached. The controller maps this to HTTP 429 + Retry-After.
/// </summary>
public class TranscodeCapacityException : Exception
{
    public TranscodeCapacityException(string message) : base(message) { }
}

/// <summary>
/// SR-WI-020/026: thrown when playlist/segment data is requested for a session whose
/// ffmpeg crashed and exhausted its retries. The controller maps this to HTTP 409 +
/// {"error":"transcode_failed"} so the client shows a real error instead of buffering
/// forever against a corpse.
/// </summary>
public class TranscodeFailedException : Exception
{
    public TranscodeFailedException(string message) : base(message) { }
}

/// <summary>
/// Manages video transcoding sessions with throttling support.
/// Registered as Singleton to maintain process tracking across all HTTP requests.
/// </summary>
public class TranscodeService : ITranscodeService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<TranscodeService> _logger;
    private readonly IProcessController _processController;
    private readonly ITranscodeSessionManager _sessionManager;
    private readonly IHlsService _hlsService;
    private readonly string _tempDir;

    // Throttling thresholds (in seconds of buffer)
    public const int ThrottleBufferMaxSeconds = 120;     // Suspend FFmpeg when buffer exceeds this
    public const int ThrottleBufferResumeSeconds = 60;   // Resume FFmpeg when buffer drops below this
    public const int HlsSegmentDurationSeconds = 6;      // Target segment duration (actual varies)
    public const int MaxCrashRetries = 3;

    // Support both .ts (MPEG-TS) and .m4s (fMP4) segment extensions
    private static readonly Regex SegmentPattern = new(@"^seg_(\d+)\.(ts|m4s)$", RegexOptions.Compiled);

    // SR-WI-028 capacity reservation (closes the check-then-add race): slots reserved
    // between the cap check and session registration, so parallel first-requests for
    // different keys can't collectively exceed the caps.
    private readonly object _capacityGate = new();
    private int _pendingStarts;
    private readonly ConcurrentDictionary<Guid, int> _pendingStartsPerUser = new();

    public TranscodeService(
        IServiceScopeFactory scopeFactory, 
        ILogger<TranscodeService> logger, 
        IProcessController processController,
        ITranscodeSessionManager sessionManager,
        IHlsService hlsService,
        IConfiguration config)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _processController = processController;
        _sessionManager = sessionManager;
        _hlsService = hlsService;
        _tempDir = Path.Combine(Directory.GetCurrentDirectory(), "transcode-temp");
        
        // Clean up temp directory on startup to remove stale sessions from previous runs
        if (Directory.Exists(_tempDir))
        {
            try 
            {
                Directory.Delete(_tempDir, true);
                _logger.LogInformation("Cleaned up temp transcode directory: {Dir}", _tempDir);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Failed to clean up temp directory on startup: {Message}", ex.Message);
            }
        }
        
        if (!Directory.Exists(_tempDir))
        {
            Directory.CreateDirectory(_tempDir);
        }
    }

    /// <summary>
    /// Get the session directory for a specific transcode session
    /// </summary>
    public string GetSessionDir(Guid mediaId, Guid userId, int? subtitleTrackIndex, string? sid = null)
    {
        // Security (audit wave-2 M-4): the client-supplied sid is concatenated into the directory
        // name, so an unvalidated value (e.g. "../../etc") would traverse out of the temp root.
        // Reject anything outside the safe charset before it touches the filesystem.
        if (!TranscodeSid.IsValid(sid))
            throw new ArgumentException("Invalid transcode session id.", nameof(sid));

        var suffix = subtitleTrackIndex.HasValue ? $"_sub{subtitleTrackIndex.Value}" : "";
        var streamSuffix = !string.IsNullOrEmpty(sid) ? $"_{sid}" : "";
        // Include userId and sid to isolate transcode sessions per stream
        var dir = Path.Combine(_tempDir, $"{mediaId}_{userId}{suffix}{streamSuffix}");

        // Defense-in-depth: even with the charset guard above, assert the resolved path stays
        // under the temp root before any caller writes/reads segments there.
        var tempRootWithSep = _tempDir.EndsWith(Path.DirectorySeparatorChar)
            ? _tempDir : _tempDir + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(dir).StartsWith(Path.GetFullPath(tempRootWithSep), StringComparison.Ordinal))
            throw new ArgumentException("Resolved transcode session path escapes the temp root.", nameof(sid));

        return dir;
    }

    /// <summary>
    /// Get the temp directory path
    /// </summary>
    public string GetTempDir() => _tempDir;

    /// <summary>
    /// Get all active sessions (for monitoring service)
    /// </summary>
    public IEnumerable<TranscodeSession> GetAllSessions() => _sessionManager.GetAllSessions();

    /// <summary>
    /// Get a specific session by key
    /// </summary>
    public TranscodeSession? GetSession(TranscodeSessionKey key) => _sessionManager.GetSession(key);
    
    /// <summary>
    /// Get a specific session by media/user/subtitle combination
    /// </summary>
    public TranscodeSession? GetSession(Guid mediaId, Guid userId, int? subtitleTrackIndex, string? sid = null) => 
        _sessionManager.GetSession(mediaId, userId, subtitleTrackIndex, sid);

    /// <summary>
    /// Extract segment index from filename like "seg_042.ts" or "seg_042.m4s"
    /// Kept static for compatibility, logic duplicated from HlsService for now.
    /// </summary>
    public static int ExtractSegmentIndex(string segmentName)
    {
        var match = SegmentPattern.Match(segmentName);
        return match.Success ? int.Parse(match.Groups[1].Value) : -1;
    }

    /// <summary>
    /// Get the latest segment index from disk
    /// </summary>
    public int GetLatestSegmentIndex(string sessionDir) => _hlsService.GetLatestSegmentIndex(sessionDir);

    /// <summary>Parsed session-playlist facts (segment count, duration, ENDLIST) — used by
    /// the throttle monitor to tell normal completion from a crash (SR-WI-020).</summary>
    public HlsPlaylistInfo? GetPlaylistInfo(string sessionDir) => _hlsService.GetPlaylistInfo(sessionDir);

    /// <summary>
    /// Parse the HLS playlist to get actual cumulative duration up to segmentCount segments.
    /// </summary>
    public double GetActualPlaylistDuration(string sessionDir, int segmentCount) => 
        _hlsService.GetActualPlaylistDuration(sessionDir, segmentCount);

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
        var session = _sessionManager.GetSession(key);
        if (session != null)
        {
            session.ClientSegmentIndex = segmentIndex;
            session.LastClientRequestTime = DateTime.UtcNow;
            // NOTE: CrashRetryCount deliberately NOT reset here (SR-WI-020) — resetting on
            // mere client activity let a crash-looping source retry forever. The monitor
            // resets it once transcoding progresses past the crash point.
            session.IsPaused = false;    // Client is actively requesting - wake from DORMANT if needed

            // SR-WI-020: a dormant session's ffmpeg was killed on purpose; nothing used to
            // restart it, so the client drained the buffer into an infinite stall. Kick a
            // background revival (idempotent, takes the session lock) as soon as segments
            // are being consumed again.
            if (session.State == TranscodeState.Dormant
                && (session.Process == null || session.Process.HasExited))
            {
                _ = Task.Run(() => TryReviveSessionAsync(key, countAsCrashRetry: false));
            }

            _logger.LogDebug("Client position updated: {MediaId} -> segment {Index}", key.MediaId, segmentIndex);
        }
    }

    /// <summary>
    /// Set paused state for a session
    /// </summary>
    public bool SetPaused(TranscodeSessionKey key, Guid userId, bool isPaused)
    {
        var session = _sessionManager.GetSession(key);
        if (session != null)
        {
            // Validate ownership
            if (session.UserId != userId)
            {
                _logger.LogWarning("User {UserId} attempted to pause session owned by {OwnerId}", userId, session.UserId);
                return false;
            }
            
            session.IsPaused = isPaused;
            session.LastClientRequestTime = DateTime.UtcNow;

            // SR-WI-020: unpausing a dormant session proactively restarts ffmpeg so the
            // buffer refills BEFORE the client drains it (instead of stalling ~2 minutes
            // after resume, which was the single most common playback failure).
            if (!isPaused && session.State == TranscodeState.Dormant
                && (session.Process == null || session.Process.HasExited))
            {
                _ = Task.Run(() => TryReviveSessionAsync(key, countAsCrashRetry: false));
            }

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
        int? maxBitrate = null,
        bool? burnSubtitles = null,
        string? sid = null,
        bool remux = false,
        bool audioCopy = false,
        string? audioCodec = null,
        int audioChannels = 0)
    {
        // Sanitize subtitle track index: if negative, treat as null (disabled)
        if (subtitleTrackIndex.HasValue && subtitleTrackIndex.Value < 0)
        {
            subtitleTrackIndex = null;
        }

        // Check concurrent transcode limits from settings (global + per-user). An
        // over-cap request throws TranscodeCapacityException, which the controller maps
        // to 429 + Retry-After. Only count sessions that are NOT this exact session key
        // (a re-request for an already-running stream must not be rejected as "new").
        int maxConcurrent, maxPerUser;
        using (var scope = _scopeFactory.CreateScope())
        {
            var settingsService = scope.ServiceProvider.GetRequiredService<ISettingsService>();
            var maxConcurrentCfg = await settingsService.GetSettingAsync("MaxSimultaneousTranscodes", 0);
            var maxPerUserCfg = await settingsService.GetSettingAsync("MaxSimultaneousTranscodesPerUser", 3);

            // Audit wave-2 L-14: a config value of 0 (or a huge value) must NOT disable the DoS
            // bound. Always enforce a finite HARD ceiling that config can clamp DOWN but never
            // remove — ffmpeg is CPU-bound, so these are already extreme for home hardware.
            const int HardGlobalCeiling = 16;
            const int HardPerUserCeiling = 6;
            maxConcurrent = maxConcurrentCfg > 0 ? Math.Min(maxConcurrentCfg, HardGlobalCeiling) : HardGlobalCeiling;
            maxPerUser = maxPerUserCfg > 0 ? Math.Min(maxPerUserCfg, HardPerUserCeiling) : 3;
        }

        var sessionKey = new TranscodeSessionKey(mediaId, userId, subtitleTrackIndex, sid);

        // SR-WI-028: check-then-add raced — parallel first requests for DIFFERENT keys could
        // each pass the count and exceed the cap. Reserve a slot under a gate; the reservation
        // is released once the session is registered (or the attempt fails/returns existing).
        lock (_capacityGate)
        {
            bool IsActiveOther(TranscodeSession s) =>
                s.State != TranscodeState.Dormant && s.State != TranscodeState.Completed
                && s.State != TranscodeState.Failed && !s.Key.Equals(sessionKey);

            var active = _sessionManager.GetAllSessions().Where(IsActiveOther).ToList();

            if (active.Count + _pendingStarts >= maxConcurrent)
            {
                _logger.LogWarning("Global max concurrent transcodes ({Max}) reached, rejecting {MediaId}", maxConcurrent, mediaId);
                throw new TranscodeCapacityException(
                    $"Server transcode limit ({maxConcurrent}) reached. Try again shortly.");
            }

            var userPending = _pendingStartsPerUser.GetValueOrDefault(userId);
            if (active.Count(s => s.UserId == userId) + userPending >= maxPerUser)
            {
                _logger.LogWarning("Per-user max concurrent transcodes ({Max}) reached for user {UserId}", maxPerUser, userId);
                throw new TranscodeCapacityException(
                    $"Your transcode limit ({maxPerUser}) is reached. Stop another stream and try again.");
            }

            _pendingStarts++;
            _pendingStartsPerUser.AddOrUpdate(userId, 1, (_, v) => v + 1);
        }

        try
        {
        using (await _sessionManager.AcquireLockAsync(sessionKey))
        {
            // Check if session already exists
            var existingSession = _sessionManager.GetSession(sessionKey);
            if (existingSession != null)
            {
                // Verify the session directory still exists (could have been cleaned up)
                if (Directory.Exists(existingSession.SessionDirectory))
                {
                    // Check for parameter changes that require a restart
                    bool parametersChanged = false;
                    var restartReason = "";

                    // Normalize resolution for comparison (handle nulls as "original")
                    var newResolution = resolution ?? "original";
                    if (!string.Equals(existingSession.TargetResolution, newResolution, StringComparison.OrdinalIgnoreCase))
                    {
                        parametersChanged = true;
                        restartReason = $"Resolution changed from {existingSession.TargetResolution} to {newResolution}";
                    }
                    else if (existingSession.TargetCodec != codec) // Nulls match nulls
                    {
                        parametersChanged = true;
                        restartReason = $"Codec changed from {existingSession.TargetCodec ?? "auto"} to {codec ?? "auto"}";
                    }
                    else if (existingSession.PreserveHdr != (preserveHdr ?? false))
                    {
                        parametersChanged = true;
                        restartReason = $"HDR preference changed from {existingSession.PreserveHdr} to {preserveHdr ?? false}";
                    }
                    else if (existingSession.AudioTrackIndex != audioTrack)
                    {
                         parametersChanged = true;
                         restartReason = $"Audio track changed from {existingSession.AudioTrackIndex} to {audioTrack}";
                    }
                    else if (existingSession.MaxBitrate != maxBitrate)
                    {
                         parametersChanged = true;
                         restartReason = $"Max bitrate changed from {existingSession.MaxBitrate} to {maxBitrate}";
                    }
                     else if (existingSession.SeekPosition != seekPosition)
                     {
                          parametersChanged = true;
                          restartReason = $"Seek position changed from {existingSession.SeekPosition} to {seekPosition}";
                     }
                     // Check if burn subtitles preference changed
                     else if (existingSession.BurnSubtitles != (burnSubtitles ?? false))
                     {
                         parametersChanged = true;
                         restartReason = $"Burn subtitles preference changed from {existingSession.BurnSubtitles} to {burnSubtitles ?? false}";
                     }
                     // A switch between remux (stream-copy) and transcode requires a fresh ffmpeg (R-WI-003)
                     else if (existingSession.IsRemux != remux)
                     {
                         parametersChanged = true;
                         restartReason = $"Playback method changed (remux={existingSession.IsRemux} -> {remux})";
                     }
                     // An audio-decision change (copy/codec/channels) requires a fresh ffmpeg (R-WI-004)
                     else if (existingSession.AudioCopy != audioCopy ||
                              existingSession.AudioCodec != audioCodec ||
                              existingSession.AudioChannels != audioChannels)
                     {
                         parametersChanged = true;
                         restartReason = $"Audio decision changed (copy={existingSession.AudioCopy}->{audioCopy}, codec={existingSession.AudioCodec}->{audioCodec}, ch={existingSession.AudioChannels}->{audioChannels})";
                     }

                     if (parametersChanged)
                    {
                        _logger.LogInformation("{Reason} for {MediaId}, restarting transcode", restartReason, mediaId);
                        await StopSessionInternalAsync(existingSession, sessionKey);
                    }
                    else
                    {
                        // SR-WI-020: an "already active and valid" session may be a corpse —
                        // dormant park, ffmpeg crash, or a mislabeled completion. Returning it
                        // unexamined replayed the same frozen playlist forever (the infinite
                        // "Buffering..." after a pause). Check liveness before trusting it.
                        bool needsFullRestart = false;
                        if (existingSession.Process == null || existingSession.Process.HasExited)
                        {
                            var playlistInfo = _hlsService.GetPlaylistInfo(existingSession.SessionDirectory);
                            if (playlistInfo is { HasEndList: true })
                            {
                                // Fully transcoded — the finished playlist serves from disk.
                                existingSession.State = TranscodeState.Completed;
                            }
                            else if (existingSession.IsPaused)
                            {
                                // Parked on purpose (paused / walked away). hls.js reloads
                                // master.m3u8 periodically EVEN WHILE PAUSED, and reviving on
                                // those reloads churned ffmpeg endlessly (live-QA 2026-07-24:
                                // 29 revive→dormant cycles during one long pause, each rewrite
                                // window bleeding the client's reconnect budget). Serve the
                                // parked playlist untouched; real resumption revives via
                                // /resume (SetPaused) or the first segment request
                                // (UpdateClientPosition), both of which clear IsPaused.
                            }
                            else
                            {
                                // A fresh master.m3u8 request is explicit client intent: reset
                                // any exhausted crash budget and try to continue where the
                                // playlist left off (append revival — no re-transcode of what
                                // already exists).
                                existingSession.CrashRetryCount = 0;
                                if (existingSession.State == TranscodeState.Failed)
                                    existingSession.State = TranscodeState.Transcoding;

                                var revived = await ReviveSessionCoreAsync(existingSession, playlistInfo);
                                if (revived)
                                {
                                    existingSession.IsPaused = false;
                                }
                                else
                                {
                                    _logger.LogWarning(
                                        "Could not revive dead session for {MediaId}; falling back to full restart", mediaId);
                                    await StopSessionInternalAsync(existingSession, sessionKey);
                                    needsFullRestart = true;
                                }
                            }
                        }

                        if (needsFullRestart)
                        {
                            // fall through to fresh-session creation below
                        }
                        else
                        {
                        _logger.LogDebug("Transcode session already active and valid for {MediaId}", mediaId);

                        // Check if subtitles were requested but not extracted (logic retained from original)
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
                                    // Serve only if the seek alignment succeeded (R-WI-018 review:
                                    // absolute cues on an offset stream are worse than none).
                                    var aligned = !(existingSession.SeekPosition.HasValue && existingSession.SeekPosition.Value > 0)
                                                  || ffmpegService.OffsetWebVttTimestamps(vttPath, existingSession.SeekPosition.Value);
                                    existingSession.SubtitleVttPath = aligned ? vttPath : null;
                                    if (!aligned)
                                        _logger.LogError("VTT offset failed for session {MediaId} — subtitles disabled for this stream", mediaId);
                                }
                                return existingSession;
                            }
                        }
                        else
                        {
                            return existingSession;
                        }

                        return existingSession;
                        } // end !needsFullRestart
                    }
                }
                else
                {
                    // Session directory was cleaned up, remove stale session and restart
                    _logger.LogInformation("Session directory missing for {MediaId}, restarting transcode", mediaId);
                    // Use TryRemoveSession to discard stale session
                    _sessionManager.TryRemoveSession(sessionKey, out _);
                }
            }

            var baseSessionDir = GetSessionDir(mediaId, userId, subtitleTrackIndex, sid);
            // Append timestamp to ensure unique directory for every session (prevents filesystem race conditions on restart)
            var sessionDir = $"{baseSessionDir}_{DateTime.UtcNow.Ticks}";
            
            // Clean up any *stale* directories from previous runs that might be lingering
            // (Optional cleanup of old versions of this session)
            
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
                MaxBitrate = maxBitrate,
                BurnSubtitles = burnSubtitles ?? false,
                IsRemux = remux,
                AudioCopy = audioCopy,
                AudioCodec = audioCodec,
                AudioChannels = audioChannels
            };

            // Detect if source is HDR for accurate reporting
            using (var scope = _scopeFactory.CreateScope())
            {
                var ffmpegService = scope.ServiceProvider.GetRequiredService<IFFmpegService>();
                var probe = await ffmpegService.ProbeMediaAsync(inputPath);
                if (probe != null)
                {
                    // Basic HDR detection logic matches StreamPlanService/TranscodeProfileBuilder
                    session.IsSourceHdr = !string.IsNullOrEmpty(probe.ColorTransfer) && 
                        (probe.ColorTransfer.Contains("smpte2084") || probe.ColorTransfer.Contains("arib-std-b67"));
                    
                    if (!session.IsSourceHdr && !string.IsNullOrEmpty(probe.PixelFormat))
                    {
                        var fmt = probe.PixelFormat.ToLowerInvariant();
                        if (fmt.Contains("10") || fmt.Contains("12") || fmt.Contains("p010") || fmt.Contains("p016"))
                        {
                            if (string.IsNullOrEmpty(probe.ColorTransfer) || probe.ColorTransfer.ToLowerInvariant() != "bt709")
                            {
                                session.IsSourceHdr = true;
                            }
                        }
                    }
                }
            }

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
                
                if (FFmpegService.IsBitmapSubtitleCodec(subtitleCodec) || (burnSubtitles == true))
                {
                    _logger.LogInformation("Subtitle track {Index} is bitmap-based ({Codec}) OR burn-in forced (Forced={Forced}) - will use burn-in overlay.", 
                        subtitleTrackIndex.Value, subtitleCodec, burnSubtitles);
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
                        // Offset timestamps if seeking - VTT has absolute times but HLS plays from 0.
                        // Serve only if that alignment succeeded (R-WI-018 review: absolute cues on
                        // an offset stream are off by the whole seek — worse than no subtitles).
                        var aligned = !(seekPosition.HasValue && seekPosition.Value > 0)
                                      || ffmpegService.OffsetWebVttTimestamps(vttPath, seekPosition.Value);
                        session.SubtitleVttPath = aligned ? vttPath : null;
                        if (aligned)
                            _logger.LogInformation("Subtitle extracted successfully for session {MediaId}", mediaId);
                        else
                            _logger.LogError("VTT offset failed for session {MediaId} — subtitles disabled for this stream", mediaId);
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
            _sessionManager.TryAddSession(session);
            
            _logger.LogInformation("Session {MediaId} added to active sessions. SubtitleVttPath={Path}",
                mediaId, session.SubtitleVttPath ?? "null");
            
            // SR-WI-028: wait for the playlist to appear instead of a fixed 3s sleep —
            // fast starts (remux, hw encode) return in a few hundred ms, slow ones get
            // up to 15s before we hand back a session whose playlist may 404 briefly.
            var playlistPath = Path.Combine(sessionDir, "master.m3u8");
            var pollDeadline = DateTime.UtcNow.AddSeconds(15);
            while (DateTime.UtcNow < pollDeadline)
            {
                try
                {
                    if (File.Exists(playlistPath) && new FileInfo(playlistPath).Length > 0) break;
                }
                catch (IOException) { /* transient — keep polling */ }
                if (process.HasExited) break; // startup failure — no point waiting out the clock
                await Task.Delay(200);
            }

            return session;
        }
        }
        finally
        {
            // Release the SR-WI-028 capacity reservation. The registered session (if any)
            // now counts as active on its own; a failed/early-returned attempt frees the slot.
            lock (_capacityGate)
            {
                _pendingStarts--;
                _pendingStartsPerUser.AddOrUpdate(userId, 0, (_, v) => Math.Max(0, v - 1));
            }
        }
    }

    /// <summary>
    /// Suspend the FFmpeg process for a session (throttle when buffer is full).
    /// </summary>
    public bool SuspendSession(TranscodeSessionKey key)
    {
        var session = _sessionManager.GetSession(key);
        if (session == null) return false;

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
    /// </summary>
    public bool ResumeSession(TranscodeSessionKey key)
    {
        var session = _sessionManager.GetSession(key);
        if (session == null) return false;

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
    /// SR-WI-020 — rebase the builder's HLS muxer options so a revived ffmpeg APPENDS to the
    /// existing playlist from the next segment index instead of starting a new one. Relies on
    /// the builder always emitting "-start_number 0"; returns null when the token is absent
    /// (caller falls back to a full restart rather than corrupting the playlist).
    /// </summary>
    public static string? ApplyResumeArgs(string arguments, int startNumber)
    {
        const string freshToken = "-start_number 0 ";
        var idx = arguments.IndexOf(freshToken, StringComparison.Ordinal);
        if (idx < 0) return null;

        arguments = arguments.Remove(idx, freshToken.Length)
            .Insert(idx, $"-start_number {startNumber} ");

        if (!arguments.Contains("append_list", StringComparison.Ordinal))
        {
            var flagsMatch = Regex.Match(arguments, @"-hls_flags (\S+)");
            arguments = flagsMatch.Success
                ? arguments.Replace(flagsMatch.Value, $"-hls_flags {flagsMatch.Groups[1].Value}+append_list")
                : arguments.Insert(idx, "-hls_flags append_list ");
        }

        return arguments;
    }

    /// <summary>
    /// SR-WI-020 — restart ffmpeg for a session whose process died (dormant park after a
    /// pause, a crash, or host restart races), appending to the existing playlist from the
    /// last completed segment so nothing already transcoded is redone. Caller MUST hold the
    /// per-key session lock. Returns false when revival isn't possible (missing dir/playlist
    /// or unpatchable args) — callers fall back to a full restart or mark the session Failed.
    /// </summary>
    private async Task<bool> ReviveSessionCoreAsync(TranscodeSession session, HlsPlaylistInfo? playlistInfo)
    {
        if (!Directory.Exists(session.SessionDirectory)) return false;
        playlistInfo ??= _hlsService.GetPlaylistInfo(session.SessionDirectory);
        if (playlistInfo == null) return false;

        var resumeSeek = (session.SeekPosition ?? 0) + playlistInfo.TotalDurationSeconds;
        var process = await StartFFmpegProcessAsync(session, resumeSeek, resumeStartNumber: playlistInfo.SegmentCount);
        if (process == null) return false;

        session.Process = process;
        session.State = TranscodeState.Transcoding;
        session.IsSuspended = false;
        _logger.LogInformation(
            "Revived session {MediaId}: appending from segment {Seg} (t={Seek:F1}s, {Count} segments already on disk)",
            session.Key.MediaId, playlistInfo.SegmentCount, resumeSeek, playlistInfo.SegmentCount);
        return true;
    }

    /// <summary>
    /// SR-WI-020 — public revival entry for the throttle monitor (crash retry) and the
    /// resume/segment paths (dormant wake). Takes the per-key lock; idempotent when the
    /// process is already alive.
    /// </summary>
    public async Task<bool> TryReviveSessionAsync(TranscodeSessionKey key, bool countAsCrashRetry)
    {
        using (await _sessionManager.AcquireLockAsync(key))
        {
            var session = _sessionManager.GetSession(key);
            if (session == null) return false;
            if (session.Process is { HasExited: false }) return true; // already alive
            if (session.State == TranscodeState.Failed) return false; // client must re-request explicitly

            var playlistInfo = _hlsService.GetPlaylistInfo(session.SessionDirectory);
            if (playlistInfo is { HasEndList: true })
            {
                session.State = TranscodeState.Completed; // fully transcoded — serves from disk
                return true;
            }

            if (countAsCrashRetry)
            {
                session.LastCrashSegmentIndex = session.LatestSegmentIndex;
                session.CrashRetryCount++;
                if (session.CrashRetryCount > MaxCrashRetries)
                {
                    session.State = TranscodeState.Failed;
                    _logger.LogError(
                        "Session {MediaId} FAILED: ffmpeg crashed {Count} times without progress; giving up until the client re-requests",
                        session.Key.MediaId, session.CrashRetryCount - 1);
                    return false;
                }
            }

            var revived = await ReviveSessionCoreAsync(session, playlistInfo);
            if (revived) session.IsPaused = false;
            return revived;
        }
    }

    /// <summary>
    /// Start FFmpeg process with current session settings
    /// </summary>
    private async Task<Process?> StartFFmpegProcessAsync(TranscodeSession session, double? seekPosition, int? resumeStartNumber = null)
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
        
        // R-WI-003: a Remux plan copies the compatible A/V streams (no re-encode) via a distinct
        // arg path. Bitmap-subtitle burn-in requires a real encode, so it never takes this branch
        // (CanRemux only picks Remux when video+audio are already client-compatible; text subs ride
        // as sidecar VTT). Fall back to the transcode path if a burn-in was somehow requested.
        ProcessStartInfo startInfo;
        if (session.IsRemux && subtitleBurnInIndex == null)
        {
            startInfo = ffmpegService.GetRemuxArguments(
                session.InputPath,
                session.SessionDirectory,
                "seg",
                seekPosition,
                session.AudioTrackIndex);
        }
        else
        {
            startInfo = await ffmpegService.GetTranscodeArgumentsAsync(
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
                session.MaxBitrate,        // Pass max bitrate from session
                session.AudioCopy,         // R-WI-004: audio decision from session
                session.AudioCodec,
                session.AudioChannels);
        }

        // SR-WI-020: a revival must append to the existing playlist, not restart it.
        if (resumeStartNumber.HasValue)
        {
            var patched = ApplyResumeArgs(startInfo.Arguments, resumeStartNumber.Value);
            if (patched == null)
            {
                _logger.LogWarning(
                    "Cannot patch resume args for {MediaId} (builder output changed?); revival unavailable",
                    session.Key.MediaId);
                return null;
            }
            startInfo.Arguments = patched;
        }

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
    public Stream? GetPlaylist(Guid mediaId, Guid userId, int? subtitleTrackIndex = null, string? sid = null)
    {
        var session = GetSession(mediaId, userId, subtitleTrackIndex, sid);
        ThrowIfFailed(session);
        if (session != null && Directory.Exists(session.SessionDirectory))
        {
            return _hlsService.GetPlaylistStream(session.SessionDirectory);
        }
        return null;
    }

    /// <summary>SR-WI-026: a Failed session must surface as a hard error (409), not as the
    /// 404s/stale-playlist limbo the client would retry forever.</summary>
    private static void ThrowIfFailed(TranscodeSession? session)
    {
        if (session?.State == TranscodeState.Failed)
        {
            throw new TranscodeFailedException(
                "Transcoding failed on the server for this stream (ffmpeg exited repeatedly).");
        }
    }

    /// <summary>
    /// Get a segment file from a transcode session.
    /// </summary>
    public Stream? GetSegment(Guid mediaId, Guid userId, string segmentName, int? subtitleTrackIndex = null, string? sid = null)
    {
        var session = GetSession(mediaId, userId, subtitleTrackIndex, sid);
        ThrowIfFailed(session);
        if (session != null && Directory.Exists(session.SessionDirectory))
        {
            return _hlsService.GetSegmentStream(session.SessionDirectory, segmentName);
        }
        return null; // Or handle as 404
    }

    /// <summary>
    /// Get the fMP4 initialization segment (init.mp4) for a transcode session.
    /// </summary>
    public Stream? GetInitSegment(Guid mediaId, Guid userId, int? subtitleTrackIndex = null, string? sid = null)
    {
        var session = GetSession(mediaId, userId, subtitleTrackIndex, sid);
        ThrowIfFailed(session);
        if (session != null && Directory.Exists(session.SessionDirectory))
        {
            return _hlsService.GetInitSegmentStream(session.SessionDirectory);
        }
        return null;
    }

    /// <summary>
    /// Get the sidecar VTT subtitles if available.
    /// </summary>
    public Stream? GetSubtitlesVtt(Guid mediaId, Guid userId, int? subtitleTrackIndex = null, string? sid = null)
    {
        var session = GetSession(mediaId, userId, subtitleTrackIndex, sid);
        if (session != null && !string.IsNullOrEmpty(session.SubtitleVttPath) && System.IO.File.Exists(session.SubtitleVttPath))
        {
            return System.IO.File.OpenRead(session.SubtitleVttPath);
        }
        return null;
    }

    /// <summary>
    /// Stop a specific transcode session and clean up.
    /// </summary>
    public void StopTranscode(Guid mediaId, Guid userId, int? subtitleTrackIndex = null, bool deleteFiles = true, string? sid = null)
    {
        var sessionKey = new TranscodeSessionKey(mediaId, userId, subtitleTrackIndex, sid);

        // SR-WI-025: serialize against StartTranscodeAsync's restart path — an unserialized
        // DELETE racing a far-seek restart could remove and kill the brand-new successor
        // session under the same key. Sync-over-async is acceptable here: the per-key
        // SemaphoreSlim has no thread affinity (no deadlock) and contention is rare.
        using (_sessionManager.AcquireLockAsync(sessionKey).GetAwaiter().GetResult())
        {
            if (_sessionManager.TryRemoveSession(sessionKey, out var session))
            {
                StopSession(session!, deleteFiles);
            }
        }
    }

    /// <summary>
    /// Stop all transcode sessions for a given media item and user.
    /// </summary>
    public void StopAllTranscodesForUser(Guid mediaId, Guid userId)
    {
        var allSessions = _sessionManager.GetAllSessions();
        var keysToRemove = allSessions
            .Where(s => s.Key.MediaId == mediaId && s.Key.UserId == userId)
            .Select(s => s.Key)
            .ToList();

        foreach (var key in keysToRemove)
        {
            if (_sessionManager.TryRemoveSession(key, out var session))
            {
                StopSession(session!, deleteFiles: true);
            }
        }
    }

    /// <summary>
    /// Remove session from tracking without killing process (for DORMANT state)
    /// </summary>
    public void EnterDormantState(TranscodeSessionKey key)
    {
        // SR-WI-025: hold the per-key lock so parking can't kill a process that a
        // concurrent restart/revival just created (see StopTranscode note).
        using var _ = _sessionManager.AcquireLockAsync(key).GetAwaiter().GetResult();
        var session = _sessionManager.GetSession(key);
        if (session != null)
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
        // SR-WI-025: same serialization as StopTranscode.
        using var _ = _sessionManager.AcquireLockAsync(key).GetAwaiter().GetResult();
        if (_sessionManager.TryRemoveSession(key, out var session))
        {
            StopSession(session!, deleteFiles: true);
            _logger.LogInformation("Dormant session {MediaId} deleted", key.MediaId);
        }
    }

    /// <summary>
    /// SR-WI-021 — kill every live ffmpeg on host shutdown. Without this, child processes
    /// survived server restarts, kept burning CPU/disk, and their open handles made the
    /// startup temp-purge fail silently. Segments are retained (retention cleans them);
    /// only the processes die. Called by TranscodeShutdownService.StopAsync.
    /// </summary>
    public void KillAllSessionProcesses()
    {
        foreach (var session in _sessionManager.GetAllSessions().ToList())
        {
            try
            {
                if (session.Process is { HasExited: false })
                {
                    session.Process.Kill(entireProcessTree: true);
                    session.Process.WaitForExit(2000);
                }
                session.Process?.Dispose();
                session.Process = null;
                session.State = TranscodeState.Dormant;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to kill ffmpeg for session {MediaId} during shutdown", session.Key.MediaId);
            }
        }
        _logger.LogInformation("Transcode shutdown sweep complete — no ffmpeg processes left behind");
    }

    /// <summary>
    /// Internal helper to stop a session and remove it from active sessions.
    /// Used when restarting a session with different parameters.
    /// </summary>
    private async Task StopSessionInternalAsync(TranscodeSession session, TranscodeSessionKey sessionKey)
    {
        StopSession(session, deleteFiles: true);
        _sessionManager.TryRemoveSession(sessionKey, out _);
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
