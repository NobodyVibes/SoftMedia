using Microsoft.AspNetCore.Mvc;

namespace SoftMedia.Server.Services.Transcoding;

public interface IStreamResultService
{
    /// <summary>
    /// Generates the HLS master playlist with injected tokens.
    /// </summary>
    Task<IActionResult> GenerateMasterPlaylistResultAsync(Guid mediaId, Guid userId, int? sub, string? token, string? sid = null);

    /// <summary>
    /// Serves a specific HLS segment (TS or fMP4).
    /// </summary>
    IActionResult GetSegmentResult(Guid mediaId, Guid userId, int? sub, string segment, string? sid = null);

    /// <summary>
    /// Serves the fMP4 initialization segment.
    /// </summary>
    IActionResult GetInitSegmentResult(Guid mediaId, Guid userId, int? sub, string? sid = null);

    /// <summary>
    /// Serves the WebVTT subtitle file.
    /// </summary>
    IActionResult GetSubtitleResult(Guid mediaId, Guid userId, int? sub, string? sid = null);
}
