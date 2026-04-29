using Microsoft.EntityFrameworkCore;

namespace SoftMedia.Server.Models;

/// <summary>
/// Persisted Chromaprint fingerprints for a single media item, used as input to the
/// cross-episode intro/credits detection pipeline. One row per media item; head and
/// tail fingerprints are stored separately so detection can re-run on either window
/// without re-extracting the other.
///
/// Stored as raw uint32 sequences (4 bytes per hash, ~7.8 Hz). A 5-minute window is
/// roughly 9 KB, so even a 100-episode series costs <1 MB of disk.
/// </summary>
[Index(nameof(MediaItemId), IsUnique = true)]
public class MediaFingerprint
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid MediaItemId { get; set; }
    public MediaItem? MediaItem { get; set; }

    /// <summary>
    /// Chromaprint fingerprint for the head window (start of file). Null if extraction
    /// has not run or failed for this window.
    /// </summary>
    public byte[]? HeadFingerprint { get; set; }

    /// <summary>
    /// Number of seconds from start of file covered by <see cref="HeadFingerprint"/>.
    /// </summary>
    public double HeadDurationSeconds { get; set; }

    /// <summary>
    /// Chromaprint fingerprint for the tail window (end of file). Null if extraction
    /// has not run or failed for this window.
    /// </summary>
    public byte[]? TailFingerprint { get; set; }

    /// <summary>
    /// Number of seconds from end of file covered by <see cref="TailFingerprint"/>.
    /// </summary>
    public double TailDurationSeconds { get; set; }

    public DateTime GeneratedUtc { get; set; } = DateTime.UtcNow;
}
