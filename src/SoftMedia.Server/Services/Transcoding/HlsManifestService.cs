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
    Task<byte[]> GenerateMasterPlaylistAsync(Stream basePlaylistStream, string token, string? mediaId, int? subTrackIndex, string? subtitleVttPath);
}

public class HlsManifestService : IHlsManifestService
{
    private readonly ILogger<HlsManifestService> _logger;

    public HlsManifestService(ILogger<HlsManifestService> logger)
    {
        _logger = logger;
    }

    public async Task<byte[]> GenerateMasterPlaylistAsync(Stream basePlaylistStream, string token, string? mediaId, int? subTrackIndex, string? subtitleVttPath)
    {
        using var reader = new StreamReader(basePlaylistStream, leaveOpen: true);
        var content = await reader.ReadToEndAsync();
        
        // Build query string for segments
        var queryParts = new List<string>();
        if (!string.IsNullOrEmpty(token)) queryParts.Add($"token={token}");
        if (subTrackIndex.HasValue) queryParts.Add($"sub={subTrackIndex.Value}");
        var queryString = string.Join("&", queryParts);
        
        var hasSubtitles = !string.IsNullOrEmpty(subtitleVttPath) && File.Exists(subtitleVttPath);
        
        var rewrittenContent = new StringBuilder();
        
        if (hasSubtitles && content.Contains("#EXTM3U"))
        {
            // Insert subtitle track definition
            var subtitleQueryParts = new List<string>();
            if (!string.IsNullOrEmpty(token)) subtitleQueryParts.Add($"token={token}");
            if (subTrackIndex.HasValue) subtitleQueryParts.Add($"sub={subTrackIndex.Value}");
            
            var subtitleUrl = $"/api/transcode/{mediaId}/subtitles.vtt?{string.Join("&", subtitleQueryParts)}";
            
            rewrittenContent.AppendLine("#EXTM3U");
            rewrittenContent.AppendLine($"#EXT-X-MEDIA:TYPE=SUBTITLES,GROUP-ID=\"subs\",NAME=\"Subtitles\",DEFAULT=YES,AUTOSELECT=YES,URI=\"{subtitleUrl}\"");
            
            // Append rest of content
            var restOfContent = content.Replace("#EXTM3U", "").TrimStart();
            rewrittenContent.Append(restOfContent);
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
