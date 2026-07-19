using Microsoft.Extensions.Logging;
using SoftMedia.Server.Services.Infrastructure;
using SoftMedia.Server.Services.Transcoding.Models;

namespace SoftMedia.Server.Services.Transcoding;

public class TranscodeSessionService : ITranscodeSessionService
{
    private readonly ITranscodeService _transcodeService;
    private readonly ISettingsService _settings;
    private readonly ILogger<TranscodeSessionService> _logger;

    public TranscodeSessionService(
        ITranscodeService transcodeService,
        ISettingsService settings,
        ILogger<TranscodeSessionService> logger)
    {
        _transcodeService = transcodeService;
        _settings = settings;
        _logger = logger;
    }

    public void UpdateClientPosition(Guid mediaId, Guid userId, int? sub, string segment, string? sid = null)
    {
        var segmentIndex = TranscodeService.ExtractSegmentIndex(segment);
        if (segmentIndex >= 0)
        {
            var sessionKey = new TranscodeSessionKey(mediaId, userId, sub, sid);
            _transcodeService.UpdateClientPosition(sessionKey, segmentIndex);
        }
    }

    public void SetClientDevice(Guid mediaId, Guid userId, int? sub, string? sid, Sessions.ClientDevice device)
    {
        var session = _transcodeService.GetSession(new TranscodeSessionKey(mediaId, userId, sub, sid));
        if (session != null) session.ClientDevice = device;
    }

    public TranscodeSessionResult PauseSession(Guid mediaId, Guid userId, int? sub, string? sid = null)
    {
        return SetPaused(mediaId, userId, sub, true, sid);
    }

    public TranscodeSessionResult ResumeSession(Guid mediaId, Guid userId, int? sub, string? sid = null)
    {
        return SetPaused(mediaId, userId, sub, false, sid);
    }

    private TranscodeSessionResult SetPaused(Guid mediaId, Guid userId, int? sub, bool isPaused, string? sid = null)
    {
        var sessionKey = new TranscodeSessionKey(mediaId, userId, sub, sid);
        
        if (!_transcodeService.SetPaused(sessionKey, userId, isPaused: isPaused))
        {
            var session = _transcodeService.GetSession(sessionKey);
            if (session == null)
            {
                return TranscodeSessionResult.NotFound;
            }
            if (session.UserId != userId)
            {
                return TranscodeSessionResult.Unauthorized;
            }
        }
        
        return TranscodeSessionResult.Success;
    }

    public async Task StopSession(Guid mediaId, Guid userId, int? sub, string? sid = null)
    {
        if (await ShouldRetainAsync())
        {
            // Keep segments on disk and the session resumable; the hourly cleanup prunes
            // it once its newest segment ages past the retention window.
            _transcodeService.EnterDormantState(new TranscodeSessionKey(mediaId, userId, sub, sid));
        }
        else
        {
            _transcodeService.StopTranscode(mediaId, userId, sub, sid: sid);
        }
    }

    public async Task StopAllSessions(Guid mediaId, Guid userId)
    {
        if (await ShouldRetainAsync())
        {
            foreach (var session in _transcodeService.GetAllSessions()
                         .Where(s => s.Key.MediaId == mediaId && s.Key.UserId == userId)
                         .ToList())
            {
                _transcodeService.EnterDormantState(session.Key);
            }
        }
        else
        {
            _transcodeService.StopAllTranscodesForUser(mediaId, userId);
        }
    }

    /// <summary>True when segments should be retained on close (retention &gt; 0).</summary>
    private async Task<bool> ShouldRetainAsync()
        => await _settings.GetSettingAsync("SegmentRetentionHours", 24) > 0;
}
