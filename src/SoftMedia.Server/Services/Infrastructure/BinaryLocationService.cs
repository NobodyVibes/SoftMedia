using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using System.Runtime.InteropServices;

namespace SoftMedia.Server.Services.Infrastructure;

public interface IBinaryLocationService
{
    string ResolveFFmpegPath();
    string ResolveFFprobePath();
}

public class BinaryLocationService : IBinaryLocationService
{
    private readonly ILogger<BinaryLocationService> _logger;
    private readonly IConfiguration _configuration;

    public BinaryLocationService(ILogger<BinaryLocationService> logger, IConfiguration configuration)
    {
        _logger = logger;
        _configuration = configuration;
    }

    public string ResolveFFmpegPath()
    {
        // 1. Check Configuration
        var configPath = _configuration["FFmpeg:Path"];
        if (!string.IsNullOrEmpty(configPath))
        {
            if (File.Exists(configPath))
            {
                _logger.LogDebug("Resolved ffmpeg path from configuration: {Path}", configPath);
                return configPath;
            }
            _logger.LogWarning("Configured FFmpeg path not found: {Path}. Falling back to discovery.", configPath);
        }

        // 2. Check bundled/relative locations
        var executableName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "ffmpeg.exe" : "ffmpeg";
        var candidates = new[]
        {
            Path.Combine(Directory.GetCurrentDirectory(), executableName),
            Path.Combine(Directory.GetCurrentDirectory(), "ffmpeg-bin", executableName),
            Path.Combine(Directory.GetCurrentDirectory(), "Tools", executableName),
            Path.Combine(Directory.GetCurrentDirectory(), "bin", executableName)
        };

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                _logger.LogDebug("Resolved ffmpeg path: {Path}", candidate);
                return candidate;
            }
        }

        // 3. Fallback to System PATH
        _logger.LogDebug("ffmpeg not found in common locations, falling back to system PATH 'ffmpeg'.");
        return "ffmpeg";
    }

    public string ResolveFFprobePath()
    {
        // 1. Check Configuration
        var configPath = _configuration["FFmpeg:ProbePath"];
        if (!string.IsNullOrEmpty(configPath))
        {
            if (File.Exists(configPath))
            {
                _logger.LogDebug("Resolved ffprobe path from configuration: {Path}", configPath);
                return configPath;
            }
            _logger.LogWarning("Configured FFprobe path not found: {Path}. Falling back to discovery.", configPath);
        }

        // 2. Check bundled/relative locations
        var executableName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "ffprobe.exe" : "ffprobe";
        var candidates = new[]
        {
            Path.Combine(Directory.GetCurrentDirectory(), executableName),
            Path.Combine(Directory.GetCurrentDirectory(), "ffmpeg-bin", executableName),
            Path.Combine(Directory.GetCurrentDirectory(), "Tools", executableName),
            Path.Combine(Directory.GetCurrentDirectory(), "bin", executableName)
        };

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                _logger.LogDebug("Resolved ffprobe path: {Path}", candidate);
                return candidate;
            }
        }

        // 3. Fallback to System PATH
        _logger.LogDebug("ffprobe not found in common locations, falling back to system PATH 'ffprobe'.");
        return "ffprobe";
    }
}
