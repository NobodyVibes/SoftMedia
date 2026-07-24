using System.Text.RegularExpressions;

namespace SoftMedia.Server.Services.Transcoding;

public class HlsService : IHlsService
{
    private readonly ILogger<HlsService> _logger;
    private static readonly Regex SegmentPattern = new(@"^seg_(\d+)\.(ts|m4s)$", RegexOptions.Compiled);
    public const int HlsSegmentDurationSeconds = 6; // Target segment duration (actual varies)

    public HlsService(ILogger<HlsService> logger)
    {
        _logger = logger;
    }

    public int ExtractSegmentIndex(string segmentName)
    {
        var match = SegmentPattern.Match(segmentName);
        return match.Success ? int.Parse(match.Groups[1].Value) : -1;
    }

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

    public HlsPlaylistInfo? GetPlaylistInfo(string sessionDir)
    {
        var playlistPath = Path.Combine(sessionDir, "master.m3u8");
        if (!File.Exists(playlistPath)) return null;

        try
        {
            int segments = 0;
            double total = 0;
            bool endList = false;
            foreach (var line in File.ReadAllLines(playlistPath))
            {
                if (line.StartsWith("#EXTINF:"))
                {
                    segments++;
                    var durationStr = line.Substring(8).Split(',')[0];
                    if (double.TryParse(durationStr, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var d))
                    {
                        total += d;
                    }
                }
                else if (line.StartsWith("#EXT-X-ENDLIST"))
                {
                    endList = true;
                }
            }
            return new HlsPlaylistInfo(segments, total, endList);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse playlist info for {Dir}", sessionDir);
            return null;
        }
    }

    public Stream? GetPlaylistStream(string sessionDir)
    {
        var playlistPath = Path.Combine(sessionDir, "master.m3u8");
        if (File.Exists(playlistPath))
        {
            return new FileStream(playlistPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        }
        return null;
    }

    public Stream? GetSegmentStream(string sessionDir, string segmentName)
    {
        // Validate segment name pattern (security)
        if (!SegmentPattern.IsMatch(segmentName))
        {
            _logger.LogWarning("Invalid segment name rejected: {Name}", segmentName);
            return null;
        }

        var segmentPath = Path.Combine(sessionDir, segmentName);
        if (File.Exists(segmentPath))
        {
            return new FileStream(segmentPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        }
        return null;
    }

    public Stream? GetInitSegmentStream(string sessionDir)
    {
        var initPath = Path.Combine(sessionDir, "init.mp4");
        if (File.Exists(initPath))
        {
            return new FileStream(initPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        }
        _logger.LogWarning("Init segment not found at {Path}", initPath);
        return null;
    }
}
