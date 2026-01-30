namespace SoftMedia.Server.Services.Transcoding;

public interface IHlsService
{
    double GetActualPlaylistDuration(string sessionDir, int segmentCount);
    Stream? GetPlaylistStream(string sessionDir);
    Stream? GetSegmentStream(string sessionDir, string segmentName);
    Stream? GetInitSegmentStream(string sessionDir);
    int GetLatestSegmentIndex(string sessionDir);
    int ExtractSegmentIndex(string segmentName);
}
