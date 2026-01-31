using SoftMedia.Server.Services.Transcoding.Models;
using System.Diagnostics;

namespace SoftMedia.Server.Services.Transcoding;

public interface ITranscodeService
{
    string GetSessionDir(Guid mediaId, Guid userId, int? subtitleTrackIndex);
    string GetTempDir();
    IEnumerable<TranscodeSession> GetAllSessions();
    TranscodeSession? GetSession(TranscodeSessionKey key);
    TranscodeSession? GetSession(Guid mediaId, Guid userId, int? subtitleTrackIndex);
    int GetLatestSegmentIndex(string sessionDir);
    double GetActualPlaylistDuration(string sessionDir, int segmentCount);
    int CalculateBufferSeconds(TranscodeSession session);
    void UpdateClientPosition(TranscodeSessionKey key, int segmentIndex);
    bool SetPaused(TranscodeSessionKey key, Guid userId, bool isPaused);
    
    Task<TranscodeSession?> StartTranscodeAsync(
        Guid mediaId, 
        Guid userId,
        string inputPath, 
        int? subtitleTrackIndex = null, 
        double? seekPosition = null,
        string? resolution = null,
        double? readRate = null,
        string? codec = null,
        bool? preserveHdr = null,
        int? audioTrack = null,
        int? maxBitrate = null,
        bool? burnSubtitles = null);
        
    bool SuspendSession(TranscodeSessionKey key);
    bool ResumeSession(TranscodeSessionKey key);
    
    Stream? GetPlaylist(Guid mediaId, Guid userId, int? subtitleTrackIndex = null);
    Stream? GetSegment(Guid mediaId, Guid userId, string segmentName, int? subtitleTrackIndex = null);
    Stream? GetInitSegment(Guid mediaId, Guid userId, int? subtitleTrackIndex = null);
    
    void StopTranscode(Guid mediaId, Guid userId, int? subtitleTrackIndex = null, bool deleteFiles = true);
    void StopAllTranscodesForUser(Guid mediaId, Guid userId);
    void EnterDormantState(TranscodeSessionKey key);
    void DeleteDormantSession(TranscodeSessionKey key);
}
