using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using SoftMedia.Server.Services.Transcoding.Models;

namespace SoftMedia.Server.Services.Transcoding;

public class StreamResultService : IStreamResultService
{
    private readonly ITranscodeService _transcodeService;
    private readonly IHlsManifestService _hlsManifestService;
    private readonly ILogger<StreamResultService> _logger;

    public StreamResultService(
        ITranscodeService transcodeService, 
        IHlsManifestService hlsManifestService,
        ILogger<StreamResultService> logger)
    {
        _transcodeService = transcodeService;
        _hlsManifestService = hlsManifestService;
        _logger = logger;
    }

    /// <summary>
    /// SR-WI-026: a Failed session (ffmpeg crashed, retries exhausted) surfaces as
    /// 409 + {"error":"transcode_failed"} — the contract the client's player maps to a
    /// terminal "Transcoding failed on the server" state instead of retrying forever.
    /// </summary>
    private static readonly object TranscodeFailedBody = new { error = "transcode_failed" };

    public async Task<IActionResult> GenerateMasterPlaylistResultAsync(Guid mediaId, Guid userId, int? sub, string? token, string? sid = null)
    {
        Stream? stream;
        try
        {
            stream = _transcodeService.GetPlaylist(mediaId, userId, sub, sid);
        }
        catch (TranscodeFailedException)
        {
            return new ConflictObjectResult(TranscodeFailedBody);
        }
        if (stream == null)
        {
            _logger.LogWarning("Playlist not ready for {Id} - transcoding may still be starting", mediaId);
            return new StatusCodeResult(503); // Service Unavailable
        }

        if (string.IsNullOrEmpty(token))
        {
            _logger.LogWarning("No token provided for transcode request {Id}", mediaId);
            return new FileStreamResult(stream, "application/vnd.apple.mpegurl");
        }

        // Rewrite path: We consume the stream, so we must dispose it
        using (stream)
        {
            var session = _transcodeService.GetSession(mediaId, userId, sub, sid);
            var subtitleVttPath = session?.SubtitleVttPath;
            
            var bytes = await _hlsManifestService.GenerateMasterPlaylistAsync(stream, token, mediaId.ToString(), sub, subtitleVttPath, sid);
            return new FileContentResult(bytes, "application/vnd.apple.mpegurl");
        }
    }

    public IActionResult GetSegmentResult(Guid mediaId, Guid userId, int? sub, string segment, string? sid = null)
    {
        Stream? stream;
        try
        {
            stream = _transcodeService.GetSegment(mediaId, userId, segment, sub, sid);
        }
        catch (TranscodeFailedException)
        {
            return new ConflictObjectResult(TranscodeFailedBody);
        }
        if (stream == null) return new NotFoundResult();

        // Return correct MIME type based on segment extension
        var mimeType = segment.EndsWith(".m4s", StringComparison.OrdinalIgnoreCase) 
            ? "video/mp4" 
            : "video/MP2T";
        
        return new FileStreamResult(stream, mimeType);
    }

    public IActionResult GetInitSegmentResult(Guid mediaId, Guid userId, int? sub, string? sid = null)
    {
        Stream? stream;
        try
        {
            stream = _transcodeService.GetInitSegment(mediaId, userId, sub, sid);
        }
        catch (TranscodeFailedException)
        {
            return new ConflictObjectResult(TranscodeFailedBody);
        }
        if (stream == null)
        {
            _logger.LogWarning("Init segment not found for {Id}", mediaId);
            return new NotFoundObjectResult("Initialization segment not available");
        }
        return new FileStreamResult(stream, "video/mp4");
    }

    public IActionResult GetSubtitleResult(Guid mediaId, Guid userId, int? sub, string? sid = null)
    {
        var stream = _transcodeService.GetSubtitlesVtt(mediaId, userId, sub, sid);

        if (stream == null)
        {
            _logger.LogWarning("Subtitle file not found for {Id}", mediaId);
            return new NotFoundObjectResult("Subtitle file not available");
        }

        return new FileStreamResult(stream, "text/vtt");
    }

    public IActionResult GetSubtitlePlaylistResult(Guid mediaId, Guid userId, int? sub, string? sid, string? token, double durationSeconds)
    {
        // Session must actually have a servable VTT (same rule as the raw endpoint).
        var probeStream = _transcodeService.GetSubtitlesVtt(mediaId, userId, sub, sid);
        if (probeStream == null)
        {
            return new NotFoundObjectResult("Subtitle file not available");
        }
        probeStream.Dispose();

        var queryParts = new List<string>();
        if (!string.IsNullOrEmpty(token)) queryParts.Add($"token={token}");
        if (sub.HasValue) queryParts.Add($"sub={sub.Value}");
        if (!string.IsNullOrEmpty(sid)) queryParts.Add($"sid={sid}");
        var query = string.Join("&", queryParts);

        // One segment spanning the whole stream; an over-estimated EXTINF is harmless
        // for a VOD subtitle playlist (players fetch the single VTT regardless).
        var duration = Math.Max(1, (int)Math.Ceiling(durationSeconds <= 0 ? 3600 : durationSeconds));
        var playlist = new System.Text.StringBuilder()
            .AppendLine("#EXTM3U")
            .AppendLine("#EXT-X-VERSION:3")
            .AppendLine($"#EXT-X-TARGETDURATION:{duration}")
            .AppendLine("#EXT-X-MEDIA-SEQUENCE:0")
            .AppendLine("#EXT-X-PLAYLIST-TYPE:VOD")
            .AppendLine($"#EXTINF:{duration}.0,")
            .AppendLine($"subtitles.vtt?{query}")
            .AppendLine("#EXT-X-ENDLIST")
            .ToString();

        return new ContentResult
        {
            Content = playlist,
            ContentType = "application/vnd.apple.mpegurl",
            StatusCode = 200,
        };
    }
}
