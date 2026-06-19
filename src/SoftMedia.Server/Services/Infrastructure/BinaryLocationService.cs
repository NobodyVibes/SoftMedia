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
            Path.Combine(Directory.GetCurrentDirectory(), "bin", executableName),
            // Assembly-relative (CWD-independent) — covers published / non-server-dir launches.
            Path.Combine(AppContext.BaseDirectory, executableName),
            Path.Combine(AppContext.BaseDirectory, "ffmpeg-bin", executableName),
            // jellyfin-ffmpeg apt-package install location (Linux/Docker) — NOT on PATH.
            "/usr/lib/jellyfin-ffmpeg/ffmpeg"
        };

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                _logger.LogDebug("Resolved ffmpeg path: {Path}", candidate);
                return candidate;
            }
        }

        // 3. Last-resort fallback to System PATH. WARN, not Debug: SoftMedia requires the
        // jellyfin-ffmpeg build (it has the chromaprint muxer used by intro/credits detection).
        // A bare "ffmpeg" on PATH may be a distro/Gyan build WITHOUT chromaprint, which breaks
        // fingerprinting with a non-obvious error. Set FFmpeg:Path (env FFmpeg__Path) to the
        // jellyfin-ffmpeg binary to avoid this path.
        _logger.LogWarning("ffmpeg not found in configured/known locations; falling back to system PATH " +
            "'ffmpeg'. This may resolve a build WITHOUT the chromaprint muxer — set FFmpeg:Path to a " +
            "jellyfin-ffmpeg binary if intro/credits detection misbehaves.");
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
            Path.Combine(Directory.GetCurrentDirectory(), "bin", executableName),
            // Assembly-relative (CWD-independent) — covers published / non-server-dir launches.
            Path.Combine(AppContext.BaseDirectory, executableName),
            Path.Combine(AppContext.BaseDirectory, "ffmpeg-bin", executableName),
            // jellyfin-ffmpeg apt-package install location (Linux/Docker) — NOT on PATH.
            "/usr/lib/jellyfin-ffmpeg/ffprobe"
        };

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                _logger.LogDebug("Resolved ffprobe path: {Path}", candidate);
                return candidate;
            }
        }

        // 3. Last-resort fallback to System PATH (see ResolveFFmpegPath for the chromaprint caveat).
        _logger.LogWarning("ffprobe not found in configured/known locations; falling back to system PATH " +
            "'ffprobe'. Set FFmpeg:ProbePath to a jellyfin-ffmpeg binary to be explicit.");
        return "ffprobe";
    }
}
