namespace SoftMedia.Server.Models;

/// <summary>
/// Indicates how an intro or credits timecode on a <see cref="MediaItem"/> was produced.
/// Chapter-derived values must never be overwritten by detection — the source field
/// is the gate that enforces that invariant.
/// </summary>
public enum DetectionSource
{
    Chapter = 0,
    Detected = 1
}
