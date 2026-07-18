namespace SoftMedia.Server.DTOs;

public class StreamInfoDto
{
    public string Path { get; set; } = string.Empty;
    public string ContentType { get; set; } = string.Empty;
    /// B-01 — lets the stream endpoint enforce the per-user bitrate cap for video.
    public Models.MediaType Type { get; set; }
    /// Overall source bitrate in bits/second (null when never probed).
    public long? Bitrate { get; set; }
}
