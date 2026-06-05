namespace SoftMedia.Server.Services.Media.Detection;

/// <summary>
/// Extracts Chromaprint audio fingerprints from a media file. Each fingerprint is a
/// sequence of 32-bit hashes at the Chromaprint default frame rate (~7.8 Hz). Two
/// fingerprints from different files match where their hashes have a low Hamming
/// distance per element — this is the input the cross-episode segment matcher
/// consumes to find shared intro / credits themes.
/// </summary>
public interface IFingerprintExtractor
{
    /// <summary>
    /// Effective Chromaprint hash sample rate, in hashes per second. Used by callers
    /// to convert fingerprint indices back into seconds.
    /// </summary>
    double HashesPerSecond { get; }

    /// <summary>
    /// Fingerprint the first <paramref name="durationSeconds"/> of audio from
    /// <paramref name="filePath"/>. Returns null on extraction failure (missing file,
    /// no audio stream, FFmpeg error).
    /// </summary>
    Task<uint[]?> ExtractHeadAsync(string filePath, double durationSeconds, CancellationToken cancellationToken = default);

    /// <summary>
    /// Fingerprint the last <paramref name="durationSeconds"/> of audio from
    /// <paramref name="filePath"/>. Returns null on extraction failure.
    /// </summary>
    Task<uint[]?> ExtractTailAsync(string filePath, double durationSeconds, CancellationToken cancellationToken = default);
}
