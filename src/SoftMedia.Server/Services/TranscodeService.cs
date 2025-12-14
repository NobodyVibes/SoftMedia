using System.Collections.Concurrent;
using System.Diagnostics;

namespace SoftMedia.Server.Services;

public class TranscodeService
{
    private readonly IFFmpegService _ffmpegService;
    private readonly ILogger<TranscodeService> _logger;
    private readonly ConcurrentDictionary<Guid, Process> _activeTranscodes = new();
    private readonly string _tempDir;

    public TranscodeService(IFFmpegService ffmpegService, ILogger<TranscodeService> logger, IConfiguration config)
    {
        _ffmpegService = ffmpegService;
        _logger = logger;
        _tempDir = Path.Combine(Directory.GetCurrentDirectory(), "transcode-temp");
        if (!Directory.Exists(_tempDir))
        {
            Directory.CreateDirectory(_tempDir);
        }
    }

    public async Task StartTranscodeAsync(Guid mediaId, string inputPath)
    {
        if (_activeTranscodes.ContainsKey(mediaId))
        {
            // Already transcoding
            return;
        }

        var sessionDir = Path.Combine(_tempDir, mediaId.ToString());
        var startInfo = _ffmpegService.GetTranscodeArguments(inputPath, sessionDir, "seg");

        _logger.LogInformation($"Starting transcode for {mediaId}: {startInfo.Arguments}");

        var process = new Process { StartInfo = startInfo };
        
        process.ErrorDataReceived += (sender, e) => 
        {
            if (!string.IsNullOrEmpty(e.Data))
            {
                _logger.LogDebug($"FFmpeg [{mediaId}]: {e.Data}");
            }
        };

        if (process.Start())
        {
            process.BeginErrorReadLine();
            _activeTranscodes.TryAdd(mediaId, process);
            
            // Wait for the playlist to be created (FFmpeg needs time to start and write)
            await Task.Delay(5000); 
        }
        else
        {
            _logger.LogError($"Failed to start FFmpeg for {mediaId}");
        }
    }

    public Stream? GetPlaylist(Guid mediaId)
    {
        var playlistPath = Path.Combine(_tempDir, mediaId.ToString(), "master.m3u8");
        if (File.Exists(playlistPath))
        {
            return new FileStream(playlistPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        }
        return null;
    }

    public Stream? GetSegment(Guid mediaId, string segmentName)
    {
        var segmentPath = Path.Combine(_tempDir, mediaId.ToString(), segmentName);
        if (File.Exists(segmentPath))
        {
            return new FileStream(segmentPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        }
        return null;
    }

    public void StopTranscode(Guid mediaId)
    {
        if (_activeTranscodes.TryRemove(mediaId, out var process))
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill();
                }
                process.Dispose();
                
                // Cleanup files
                var sessionDir = Path.Combine(_tempDir, mediaId.ToString());
                if (Directory.Exists(sessionDir))
                {
                    Directory.Delete(sessionDir, true);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error stopping transcode for {mediaId}");
            }
        }
    }
}
