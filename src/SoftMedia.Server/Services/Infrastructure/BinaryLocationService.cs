using Microsoft.Extensions.Logging;

namespace SoftMedia.Server.Services.Infrastructure;

public interface IBinaryLocationService
{
    string ResolveFFmpegPath();
    string ResolveFFprobePath();
}

public class BinaryLocationService : IBinaryLocationService
{
    private readonly ILogger<BinaryLocationService> _logger;

    public BinaryLocationService(ILogger<BinaryLocationService> logger)
    {
        _logger = logger;
    }

    public string ResolveFFmpegPath()
    {
        var candidates = new[]
        {
            Path.Combine(Directory.GetCurrentDirectory(), "ffmpeg-bin", "ffmpeg.exe"),
            Path.Combine(Directory.GetCurrentDirectory(), "ffmpeg.exe"),
            @"C:\Program Files\ffmpeg-2024-06-27-git-9a3bc59a38-full_build\bin\ffmpeg.exe",
            @"C:\ffmpeg\bin\ffmpeg.exe",
            @"C:\Program Files\ffmpeg\bin\ffmpeg.exe",
            @"C:\ProgramData\chocolatey\bin\ffmpeg.exe",
        };

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                _logger.LogDebug("Resolved ffmpeg path: {Path}", candidate);
                return candidate;
            }
        }

        _logger.LogDebug("ffmpeg not found in common locations, falling back to 'ffmpeg' command.");
        return "ffmpeg";
    }

    public string ResolveFFprobePath()
    {
        var candidates = new[]
        {
            Path.Combine(Directory.GetCurrentDirectory(), "ffmpeg-bin", "ffprobe.exe"),
            Path.Combine(Directory.GetCurrentDirectory(), "ffprobe.exe"),
            @"C:\Program Files\ffmpeg-2024-06-27-git-9a3bc59a38-full_build\bin\ffprobe.exe",
            @"C:\ffmpeg\bin\ffprobe.exe",
            @"C:\Program Files\ffmpeg\bin\ffprobe.exe",
            @"C:\ProgramData\chocolatey\bin\ffprobe.exe",
        };

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                _logger.LogDebug("Resolved ffprobe path: {Path}", candidate);
                return candidate;
            }
        }

        _logger.LogDebug("ffprobe not found in common locations, falling back to 'ffprobe' command.");
        return "ffprobe";
    }
}
