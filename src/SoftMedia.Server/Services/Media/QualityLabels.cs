namespace SoftMedia.Server.Services.Media;

/// <summary>
/// THE quality-label → pixel-height authority (unified 2026-08-02). Before this, two
/// parsers coexisted and disagreed on the less common labels: TranscodeController's
/// B-02 ResolutionRank knew 480p/1440p/8k/4320p while StreamPlanService's private
/// parser did not — so a ceiling hand-set to "1440p" was enforced by the plan-less
/// /stream gate but silently UNCAPPED in plan arbitration (and a 1440p session pick
/// was ignored). Every label consumer now resolves through here; if a new label is
/// ever added, it exists in exactly one switch.
/// </summary>
public static class QualityLabels
{
    /// <summary>
    /// Height in pixels for a concrete quality label; null when the label means
    /// "no cap" (null/empty, "original", "auto", or anything unrecognized — never
    /// guess a ceiling from a string we don't know).
    /// </summary>
    public static int? HeightOrNull(string? quality) => quality?.ToLowerInvariant() switch
    {
        "480p" => 480,
        "720p" => 720,
        "1080p" => 1080,
        "1440p" => 1440,
        "4k" or "2160p" => 2160,
        "8k" or "4320p" => 4320,
        _ => null,
    };

    /// <summary>
    /// Clamp-comparison ordering (B-02): uncapped labels rank highest — "original"
    /// means source quality, which must also be clamped by any configured ceiling.
    /// </summary>
    public static int Rank(string? quality) => HeightOrNull(quality) ?? int.MaxValue;
}
