using Microsoft.Extensions.Logging;
using SoftMedia.Server.Services.Transcoding.Models;

namespace SoftMedia.Server.Services.Transcoding;

public class TranscodeSessionService : ITranscodeSessionService
{
    private readonly ITranscodeService _transcodeService;
    private readonly ILogger<TranscodeSessionService> _logger;

    public TranscodeSessionService(ITranscodeService transcodeService, ILogger<TranscodeSessionService> logger)
    {
        _transcodeService = transcodeService;
        _logger = logger;
    }

    public void UpdateClientPosition(Guid mediaId, Guid userId, int? sub, string segment)
    {
        var segmentIndex = TranscodeService.ExtractSegmentIndex(segment);
        if (segmentIndex >= 0)
        {
            var sessionKey = new TranscodeSessionKey(mediaId, userId, sub);
            _transcodeService.UpdateClientPosition(sessionKey, segmentIndex);
        }
    }

    public TranscodeSessionResult PauseSession(Guid mediaId, Guid userId, int? sub)
    {
        return SetPaused(mediaId, userId, sub, true);
    }

    public TranscodeSessionResult ResumeSession(Guid mediaId, Guid userId, int? sub)
    {
        return SetPaused(mediaId, userId, sub, false);
    }

    private TranscodeSessionResult SetPaused(Guid mediaId, Guid userId, int? sub, bool isPaused)
    {
        var sessionKey = new TranscodeSessionKey(mediaId, userId, sub);
        
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

    public void StopSession(Guid mediaId, Guid userId, int? sub)
    {
        _transcodeService.StopTranscode(mediaId, userId, sub);
    }

    public void StopAllSessions(Guid mediaId, Guid userId)
    {
        _transcodeService.StopAllTranscodesForUser(mediaId, userId);
    }
}
