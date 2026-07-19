using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SoftMedia.Server.Data;
using SoftMedia.Server.Services.Sessions;
using SoftMedia.Server.Services.Transcoding;
using SoftMedia.Server.Services.Transcoding.Models;

namespace SoftMedia.Server.Controllers;

/// <summary>
/// R-WI-016 — admin "Now Playing" dashboard. Enumerates active playback sessions:
/// transcodes/remuxes from <see cref="ITranscodeService"/>'s session registry, and
/// direct plays (video direct play + all music) from <see cref="IActiveStreamRegistry"/>.
/// Terminate is scoped to TRANSCODE sessions in v1 (kills ffmpeg + removes the session,
/// which frees its concurrency-cap slot — the caps are counted from live sessions);
/// direct-play rows are read-only by design (stopping one means aborting an in-flight
/// response for a client that still holds a valid media token — explicit non-goal, §R-WI-016).
/// </summary>
[ApiController]
[Authorize(Roles = "Admin")]
[Route("api/v1/admin/sessions")]
public class AdminSessionsController : ControllerBase
{
    /// <summary>Matches ffmpeg's <c>-hls_time 6</c> — used to estimate the transcode playhead.</summary>
    private const double SegmentSeconds = 6;

    private readonly ITranscodeService _transcodeService;
    private readonly IActiveStreamRegistry _streamRegistry;
    private readonly AppDbContext _context;
    private readonly ILogger<AdminSessionsController> _logger;

    public AdminSessionsController(
        ITranscodeService transcodeService,
        IActiveStreamRegistry streamRegistry,
        AppDbContext context,
        ILogger<AdminSessionsController> logger)
    {
        _transcodeService = transcodeService;
        _streamRegistry = streamRegistry;
        _context = context;
        _logger = logger;
    }

    /// <summary>
    /// Parked sessions are only "now playing" while the client is actually pulling
    /// segments. Two parked states need the recency window:
    /// - Completed: ffmpeg finishing is NOT the viewer finishing — short files (and
    ///   the tail of any movie) are fully encoded while the client still streams
    ///   (found live: a fully-encoded clip vanished from the dashboard mid-playback);
    /// - Dormant: closing the player PARKS the session for the segment-retention
    ///   window (up to 24h) — listing it unconditionally showed a phantom "Paused"
    ///   row all day after the viewer left (review HIGH). This mirrors direct-play
    ///   idle expiry: a genuinely paused viewer drops off after ~a minute either way.
    /// </summary>
    private static readonly TimeSpan InactiveServingWindow = TimeSpan.FromSeconds(60);

    [HttpGet]
    public async Task<ActionResult<IEnumerable<ActiveSessionDto>>> GetSessions()
    {
        var now = DateTime.UtcNow;
        var transcodes = _transcodeService.GetAllSessions()
            .Where(s => (s.State != TranscodeState.Completed && s.State != TranscodeState.Dormant)
                        || now - s.LastClientRequestTime < InactiveServingWindow)
            .ToList();
        var directPlays = _streamRegistry.GetActiveEntries();

        var mediaIds = transcodes.Select(t => t.Key.MediaId)
            .Concat(directPlays.Select(d => d.MediaId))
            .Distinct()
            .ToList();
        var userIds = transcodes.Select(t => t.UserId)
            .Concat(directPlays.Select(d => d.UserId))
            .Distinct()
            .ToList();

        var media = await _context.MediaItems
            .Where(m => mediaIds.Contains(m.Id))
            .Select(m => new { m.Id, m.Title, m.Duration })
            .ToDictionaryAsync(m => m.Id);
        var users = await _context.Users
            .Where(u => userIds.Contains(u.Id))
            .Select(u => new { u.Id, u.Username })
            .ToDictionaryAsync(u => u.Id);

        var rows = new List<ActiveSessionDto>();

        foreach (var s in transcodes)
        {
            media.TryGetValue(s.Key.MediaId, out var m);
            users.TryGetValue(s.UserId, out var u);
            rows.Add(new ActiveSessionDto(
                Type: s.IsRemux ? "Remux" : "Transcode",
                State: s.State switch
                {
                    TranscodeState.Completed => "Serving",  // fully encoded, client still streaming
                    TranscodeState.Dormant => "Paused",
                    TranscodeState.Throttled => "Transcoding",  // buffer-full backpressure is an internal detail
                    _ => s.State.ToString(),
                },
                UserId: s.UserId,
                UserName: u?.Username ?? "(deleted user)",
                MediaId: s.Key.MediaId,
                MediaTitle: m?.Title ?? "(removed item)",
                // Playhead estimate: the session's start offset plus the segments the
                // CLIENT has actually requested (LatestSegmentIndex is how far ffmpeg
                // got, not how far the viewer is). Clamped into [0, duration] — near
                // the end the estimate can overshoot by the client's prefetch buffer.
                PositionSeconds: ClampPosition((s.SeekPosition ?? 0) + s.ClientSegmentIndex * SegmentSeconds, m?.Duration ?? 0),
                DurationSeconds: m?.Duration ?? 0,
                StartedAt: s.SessionStartTime,
                Resolution: s.TargetResolution,
                Codec: s.TargetCodec,
                MaxBitrateKbps: s.MaxBitrate,
                CanTerminate: true,
                SubtitleTrackIndex: s.Key.SubtitleTrackIndex,
                StreamId: s.Key.StreamId,
                DeviceType: s.ClientDevice?.DeviceType,
                IpAddress: s.ClientDevice?.IpAddress));
        }

        foreach (var d in directPlays)
        {
            media.TryGetValue(d.MediaId, out var m);
            users.TryGetValue(d.UserId, out var u);
            rows.Add(new ActiveSessionDto(
                Type: "DirectPlay",
                // No beat yet = an open stream that may not be playback (e.g. the
                // music player PRELOADS the next track through /stream).
                State: d.HasHeartbeat ? "Playing" : "Streaming",
                UserId: d.UserId,
                UserName: u?.Username ?? "(deleted user)",
                MediaId: d.MediaId,
                MediaTitle: m?.Title ?? "(removed item)",
                PositionSeconds: d.PositionSeconds,
                DurationSeconds: m?.Duration ?? 0,
                StartedAt: d.StartedAt,
                Resolution: null,
                Codec: null,
                MaxBitrateKbps: null,
                CanTerminate: false,
                SubtitleTrackIndex: null,
                StreamId: null,
                DeviceType: d.Device?.DeviceType,
                IpAddress: d.Device?.IpAddress));
        }

        return Ok(rows.OrderByDescending(r => r.StartedAt));
    }

    /// <summary>
    /// Terminate a transcode session by its full session key. 404 when no such session
    /// is live (already ended, or the caller passed a direct-play row's identifiers —
    /// direct plays have no server-side session to kill).
    /// </summary>
    [HttpDelete]
    public IActionResult Terminate(
        [FromQuery] Guid mediaId,
        [FromQuery] Guid userId,
        [FromQuery] int? sub = null,
        [FromQuery] string? sid = null)
    {
        // "-1 means none" convention, matching every TranscodeController entry point.
        if (sub is < 0) sub = null;

        var key = new TranscodeSessionKey(mediaId, userId, sub, sid);
        var exists = _transcodeService.GetAllSessions().Any(s => s.Key.Equals(key));
        if (!exists) return NotFound(new { error = "No live transcode session matches that key." });

        _logger.LogInformation(
            "Admin {Admin} terminated transcode session media={MediaId} user={UserId} sub={Sub} sid={Sid}",
            User.Identity?.Name, mediaId, userId, sub, sid);
        _transcodeService.StopTranscode(mediaId, userId, sub, deleteFiles: true, sid);
        return NoContent();
    }

    private static double ClampPosition(double position, double duration) =>
        duration > 0 ? Math.Clamp(position, 0, duration) : Math.Max(0, position);
}

public record ActiveSessionDto(
    string Type,
    string State,
    Guid UserId,
    string UserName,
    Guid MediaId,
    string MediaTitle,
    double PositionSeconds,
    double DurationSeconds,
    DateTime StartedAt,
    string? Resolution,
    string? Codec,
    int? MaxBitrateKbps,
    bool CanTerminate,
    int? SubtitleTrackIndex,
    string? StreamId,
    /// Coarse client form factor ("Mobile"/"Tablet"/"Tv"/"Cast"/"Desktop"/"Unknown") derived
    /// from the User-Agent, and the client address. Null when the session predates any request
    /// that carried them (e.g. an entry restored without an HttpContext).
    string? DeviceType,
    string? IpAddress);
