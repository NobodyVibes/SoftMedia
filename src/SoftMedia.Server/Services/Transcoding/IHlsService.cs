namespace SoftMedia.Server.Services.Transcoding;

/// <summary>Parsed session-playlist facts for revival decisions (SR-WI-020).</summary>
public record HlsPlaylistInfo(int SegmentCount, double TotalDurationSeconds, bool HasEndList);

public interface IHlsService
{
    double GetActualPlaylistDuration(string sessionDir, int segmentCount);
    HlsPlaylistInfo? GetPlaylistInfo(string sessionDir);
    Stream? GetPlaylistStream(string sessionDir);
    Stream? GetSegmentStream(string sessionDir, string segmentName);
    Stream? GetInitSegmentStream(string sessionDir);
    int GetLatestSegmentIndex(string sessionDir);
    int ExtractSegmentIndex(string segmentName);
}
