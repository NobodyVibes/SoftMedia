namespace SoftMedia.Server.Services.Dlna;

/// <summary>DLNA protocolInfo flag strings and the advertised Source protocol set.</summary>
public static class DlnaProtocol
{
    // DLNA.ORG_OP=01 → byte-range seek supported; CI=0 → not transcoded; FLAGS → streaming
    // mode + background-transfer + connection-stall (the common "play and seek" flag string).
    public const string VideoFlags = "DLNA.ORG_OP=01;DLNA.ORG_CI=0;DLNA.ORG_FLAGS=01700000000000000000000000000000";
    public const string AudioFlags = "DLNA.ORG_OP=01;DLNA.ORG_CI=0;DLNA.ORG_FLAGS=01700000000000000000000000000000";

    /// <summary>Value for ConnectionManager GetProtocolInfo "Source" — what this server can serve.</summary>
    public static readonly string SourceProtocolInfo = string.Join(",", new[]
    {
        "video/mp4", "video/x-matroska", "video/x-msvideo", "video/avi", "video/mpeg",
        "video/quicktime", "video/webm", "video/x-flv", "video/3gpp", "video/MP2T",
        "audio/mpeg", "audio/mp4", "audio/flac", "audio/x-flac", "audio/ogg",
        "audio/wav", "audio/x-wav", "audio/aac",
    }.Select(m => $"http-get:*:{m}:*"));
}
