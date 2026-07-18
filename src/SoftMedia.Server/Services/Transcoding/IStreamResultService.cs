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

    /// <summary>
    /// B-13/B-14 — a compliant single-segment WebVTT MEDIA PLAYLIST wrapping the
    /// session's VTT, referenced by the master's subtitle rendition (native HLS
    /// players need a playlist, not a raw .vtt).
    /// </summary>
    IActionResult GetSubtitlePlaylistResult(Guid mediaId, Guid userId, int? sub, string? sid, string? token, double durationSeconds);
}
