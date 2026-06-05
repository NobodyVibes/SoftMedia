using SoftMedia.Server.Models;

namespace SoftMedia.Server.Services.Media;

/// <summary>
/// Service for determining the optimal audio streaming strategy.
/// Compares source audio codec against client-declared capabilities.
/// </summary>
public interface IAudioStreamPlanService
{
    /// <summary>
    /// Compute the optimal stream plan for an audio track.
    /// </summary>
    /// <param name="mediaId">The media item ID</param>
    /// <param name="clientAudioCodecs">Audio codecs the client supports (e.g., ["aac", "mp3", "flac"])</param>
    /// <param name="clientMaxBitrate">Client's max bitrate preference in kbps (0 = unlimited)</param>
    /// <returns>The computed stream plan</returns>
    Task<AudioStreamPlan> ComputePlanAsync(Guid mediaId, string[] clientAudioCodecs, int clientMaxBitrate);
}

/// <summary>
/// Result of audio stream planning.
/// </summary>
public record AudioStreamPlan
{
    /// <summary>Whether the source format can be played directly by the client.</summary>
    public bool CanDirectPlay { get; init; }
    
    /// <summary>Source audio codec (e.g., "flac", "mp3").</summary>
    public required string SourceCodec { get; init; }
    
    /// <summary>Target codec for transcoding (null if direct play).</summary>
    public string? TargetCodec { get; init; }
    
    /// <summary>Target bitrate in kbps for transcoding (null = default/lossless).</summary>
    public int? TargetBitrate { get; init; }
    
    /// <summary>URL to stream the audio.</summary>
    public required string Url { get; init; }
    
    /// <summary>MIME content type for the response.</summary>
    public required string ContentType { get; init; }
    
    /// <summary>Absolute file path (for direct play only).</summary>
    public string? FilePath { get; init; }
    
    /// <summary>Duration in seconds.</summary>
    public double Duration { get; init; }
}
