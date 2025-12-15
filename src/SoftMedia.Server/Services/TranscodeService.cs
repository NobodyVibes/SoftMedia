using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace SoftMedia.Server.Services;

/// <summary>
/// Key for tracking unique transcode sessions (mediaId + subtitle track combination)
/// </summary>
public record TranscodeSessionKey(Guid MediaId, int? SubtitleTrackIndex);

/// <summary>
/// Manages video transcoding sessions. Registered as Singleton to maintain process tracking
/// across all HTTP requests. Uses IServiceScopeFactory to access scoped services like IFFmpegService.
/// </summary>
public class TranscodeService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<TranscodeService> _logger;
    private readonly ConcurrentDictionary<TranscodeSessionKey, Process> _activeTranscodes = new();
    private readonly ConcurrentDictionary<TranscodeSessionKey, SemaphoreSlim> _sessionLocks = new();
    private readonly string _tempDir;

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
    private string GetSessionDir(Guid mediaId, int? subtitleTrackIndex)
    {
        var suffix = subtitleTrackIndex.HasValue ? $"_sub{subtitleTrackIndex.Value}" : "";
        return Path.Combine(_tempDir, $"{mediaId}{suffix}");
    }

    /// <summary>
    /// Start transcoding with optional subtitle burn-in and seek position.
    /// Uses locking to prevent multiple FFmpeg processes for the same session.
    /// </summary>
    /// <param name="mediaId">Media item ID</param>
    /// <param name="inputPath">Path to source video file</param>
    /// <param name="subtitleTrackIndex">Optional subtitle track index to burn in</param>
    /// <param name="seekPosition">Optional position in seconds to start from</param>
    public async Task StartTranscodeAsync(Guid mediaId, string inputPath, int? subtitleTrackIndex = null, double? seekPosition = null)
    {
        var sessionKey = new TranscodeSessionKey(mediaId, subtitleTrackIndex);
        
        // Get or create a lock for this specific session
        var sessionLock = _sessionLocks.GetOrAdd(sessionKey, _ => new SemaphoreSlim(1, 1));
        
        await sessionLock.WaitAsync();
        try
        {
            // Double-check if already transcoding after acquiring lock
            if (_activeTranscodes.ContainsKey(sessionKey))
            {
                _logger.LogDebug("Transcode session already active for {MediaId}", mediaId);
                return;
            }

            var sessionDir = GetSessionDir(mediaId, subtitleTrackIndex);
            
            // Clean up any existing session directory for fresh start
            if (Directory.Exists(sessionDir))
            {
                try
                {
                    Directory.Delete(sessionDir, true);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Could not clean up existing session dir: {Dir}", sessionDir);
                }
            }

            // Create a scope to resolve the scoped IFFmpegService
            using var scope = _scopeFactory.CreateScope();
            var ffmpegService = scope.ServiceProvider.GetRequiredService<IFFmpegService>();

            ProcessStartInfo startInfo;
            if (subtitleTrackIndex.HasValue || seekPosition.HasValue)
            {
                // Use the new method with subtitle/seek support
                startInfo = ffmpegService.GetTranscodeArguments(inputPath, sessionDir, "seg", subtitleTrackIndex, seekPosition);
            }
            else
            {
                // Use the basic method for simple transcoding
                startInfo = ffmpegService.GetTranscodeArguments(inputPath, sessionDir, "seg");
            }

            _logger.LogInformation("Starting transcode for {MediaId} (sub={SubTrack}, seek={Seek}): {Args}", 
                mediaId, subtitleTrackIndex, seekPosition, startInfo.Arguments);

            var process = new Process { StartInfo = startInfo };
            
            process.ErrorDataReceived += (sender, e) => 
            {
                if (!string.IsNullOrEmpty(e.Data))
                {
                    _logger.LogDebug("FFmpeg [{MediaId}]: {Data}", mediaId, e.Data);
                }
            };

            if (process.Start())
            {
                process.BeginErrorReadLine();
                _activeTranscodes.TryAdd(sessionKey, process);
                
                // Wait for the playlist to be created (FFmpeg needs time to start and write)
                await Task.Delay(5000); 
            }
            else
            {
                _logger.LogError("Failed to start FFmpeg for {MediaId}", mediaId);
            }
        }
        finally
        {
            sessionLock.Release();
        }
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
        var sessionDir = GetSessionDir(mediaId, subtitleTrackIndex);
        var segmentPath = Path.Combine(sessionDir, segmentName);
        if (File.Exists(segmentPath))
        {
            return new FileStream(segmentPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        }
        return null;
    }

    /// <summary>
    /// Stop a specific transcode session.
    /// </summary>
    public void StopTranscode(Guid mediaId, int? subtitleTrackIndex = null)
    {
        var sessionKey = new TranscodeSessionKey(mediaId, subtitleTrackIndex);
        
        if (_activeTranscodes.TryRemove(sessionKey, out var process))
        {
            StopProcess(process, GetSessionDir(mediaId, subtitleTrackIndex));
        }
    }

    /// <summary>
    /// Stop all transcode sessions for a given media item (regardless of subtitle selection).
    /// </summary>
    public void StopAllTranscodesForMedia(Guid mediaId)
    {
        var keysToRemove = _activeTranscodes.Keys.Where(k => k.MediaId == mediaId).ToList();
        foreach (var key in keysToRemove)
        {
            if (_activeTranscodes.TryRemove(key, out var process))
            {
                StopProcess(process, GetSessionDir(key.MediaId, key.SubtitleTrackIndex));
            }
        }
    }

    private void StopProcess(Process process, string sessionDir)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill();
            }
            process.Dispose();
            
            // Cleanup files
            if (Directory.Exists(sessionDir))
            {
                Directory.Delete(sessionDir, true);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error stopping transcode");
        }
    }
}
