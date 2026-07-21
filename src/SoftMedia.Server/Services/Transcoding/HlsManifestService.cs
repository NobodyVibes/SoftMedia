using Microsoft.Extensions.Logging;
using SoftMedia.Server.Services.Identity;
using SoftMedia.Server.Services.Infrastructure;
using SoftMedia.Server.Services.Media;
using SoftMedia.Server.Services.Scanning;
using SoftMedia.Server.Services.Transcoding;
using System.Text;

namespace SoftMedia.Server.Services.Transcoding;

public interface IHlsManifestService
{
    Task<byte[]> GenerateMasterPlaylistAsync(Stream basePlaylistStream, string token, string? mediaId, int? subTrackIndex, string? subtitleVttPath, string? sid = null);
}

public class HlsManifestService : IHlsManifestService
{
    private readonly ILogger<HlsManifestService> _logger;

    public HlsManifestService(ILogger<HlsManifestService> logger)
    {
        _logger = logger;
    }

    public async Task<byte[]> GenerateMasterPlaylistAsync(Stream basePlaylistStream, string token, string? mediaId, int? subTrackIndex, string? subtitleVttPath, string? sid = null)
    {
        using var reader = new StreamReader(basePlaylistStream, leaveOpen: true);
        var content = await reader.ReadToEndAsync();
        
        // Build query string for segments
        var queryParts = new List<string>();
        if (!string.IsNullOrEmpty(token)) queryParts.Add($"token={token}");
        if (subTrackIndex.HasValue) queryParts.Add($"sub={subTrackIndex.Value}");
        if (!string.IsNullOrEmpty(sid)) queryParts.Add($"sid={sid}");
        var queryString = string.Join("&", queryParts);
        
        var hasSubtitles = !string.IsNullOrEmpty(subtitleVttPath) && File.Exists(subtitleVttPath);
        
        var rewrittenContent = new StringBuilder();
        
        if (hasSubtitles && content.Contains("#EXTM3U"))
        {
            // Insert subtitle track definition and link it to the video stream
            var subtitleQueryParts = new List<string>();
            if (!string.IsNullOrEmpty(token)) subtitleQueryParts.Add($"token={token}");
            if (subTrackIndex.HasValue) subtitleQueryParts.Add($"sub={subTrackIndex.Value}");
            if (!string.IsNullOrEmpty(sid)) subtitleQueryParts.Add($"sid={sid}");
            
            // B-13: the rendition URI must be a WebVTT MEDIA PLAYLIST, not the raw
            // .vtt (hls.js tried to parse the vtt as m3u8 — wasted retries + console
            // errors — and native HLS players couldn't use it at all). B-14: a
            // compliant rendition is what gives iOS/native-HLS playback subtitles.
            // DEFAULT/AUTOSELECT are NO because the web client renders its own
            // <track> — an auto-selected rendition would double-render there; native
            // players still offer it in their subtitle UI.
            var subtitleUrl = $"/api/v1/transcode/{mediaId}/subtitles.m3u8?{string.Join("&", subtitleQueryParts)}";

            rewrittenContent.AppendLine("#EXTM3U");
            rewrittenContent.AppendLine($"#EXT-X-MEDIA:TYPE=SUBTITLES,GROUP-ID=\"subs\",NAME=\"Subtitles\",DEFAULT=NO,AUTOSELECT=NO,URI=\"{subtitleUrl}\"");
            
            // Process the rest of the lines to inject SUBTITLES attribute into stream info
            using var lineReader = new StringReader(content.Replace("#EXTM3U", "").TrimStart());
            string? line;
            while ((line = lineReader.ReadLine()) != null)
            {
                if (line.StartsWith("#EXT-X-STREAM-INF:") && !line.Contains("SUBTITLES="))
                {
                    // Append SUBTITLES attribute to link video stream to the subtitle group
                    rewrittenContent.AppendLine($"{line},SUBTITLES=\"subs\"");
                }
                else
                {
                    rewrittenContent.AppendLine(line);
                }
            }
        }
        else
        {
            rewrittenContent.Append(content);
        }
        
        // Append query string to segments
        var finalContent = rewrittenContent.ToString()
            .Replace(".ts", $".ts?{queryString}")
            .Replace(".m4s", $".m4s?{queryString}")
            .Replace("init.mp4", $"init.mp4?{queryString}");
            
        return Encoding.UTF8.GetBytes(finalContent);
    }
}
