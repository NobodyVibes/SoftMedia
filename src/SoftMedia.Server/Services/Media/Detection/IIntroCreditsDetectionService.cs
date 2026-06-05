namespace SoftMedia.Server.Services.Media.Detection;

/// <summary>
/// Outcome of running intro/credits detection on a series.
/// </summary>
/// <param name="EpisodesProcessed">Episodes considered for detection (≥2 needed to run).</param>
/// <param name="IntrosFound">Number of episodes that had an intro segment detected or
/// a chapter-derived intro left intact.</param>
/// <param name="CreditsFound">Number of episodes with a detected or chapter-derived
/// credits segment.</param>
/// <param name="FailureReason">Set when detection cannot run (e.g. fewer than 2
/// episodes, all fingerprint extractions failed). Null on success.</param>
public record IntroCreditsDetectionResult(
    int EpisodesProcessed,
    int IntrosFound,
    int CreditsFound,
    string? FailureReason);

/// <summary>
/// Cross-episode intro/credits detector. Loads all episodes for a series, extracts
/// or reuses persisted fingerprints, finds shared head/tail segments, and writes the
/// per-episode timecodes back to <see cref="Models.MediaItem"/> using
/// <see cref="Models.DetectionSource.Detected"/>. Chapter-derived values are never
/// overwritten — that invariant is enforced at the write step.
/// </summary>
public interface IIntroCreditsDetectionService
{
    Task<IntroCreditsDetectionResult> DetectAsync(Guid seriesId, CancellationToken cancellationToken = default);
}
