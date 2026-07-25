namespace SoftMedia.Server.Services.Media.Detection;

/// <summary>
/// A half-open [Start, End) segment in seconds derived from a chapter marker.
/// </summary>
public sealed record ChapterMarkerSpan(double Start, double End);

/// <summary>
/// Result of mapping a file's chapter list onto intro/credits skip segments.
/// Null members mean "no authoritative chapter for this segment" — callers fall back
/// to fingerprint detection, they must not treat null as "no intro exists".
/// </summary>
public sealed record ChapterMarkerResult(ChapterMarkerSpan? Intro, ChapterMarkerSpan? Credits)
{
    public static readonly ChapterMarkerResult Empty = new(null, null);
}

/// <summary>
/// Maps embedded chapter markers ("Intro", "Credits", …) onto intro/credits timecodes
/// (CM-WI-001). This is the single source of truth for chapter→marker semantics: both the
/// scan path (<c>VideoAnalysisStrategy</c>, from fresh probe data) and the boot-time
/// backfill (<c>ChapterMarkerBackfillService</c>, from stored <c>Chapters</c> rows) call
/// it, so the two can never disagree. Chapter-derived values are authoritative over
/// fingerprint detection (<see cref="SoftMedia.Server.Models.DetectionSource.Chapter"/>
/// wins) — the file's own authoring is ground truth; detection is a statistical fallback.
///
/// Design notes:
/// - Span ends derive from the NEXT chapter's start (last chapter → file duration), not
///   from per-chapter end metadata. For skipping, "where the next chapter begins" is the
///   ideal seek target, and it lets the backfill work from the stored (StartTime, Title)
///   schema with no migration.
/// - Title matching is deliberately conservative (exact match against a normalized keyword
///   set) because generic chapter names ("Chapter 1", "Scene 1") vastly outnumber
///   meaningful ones in real rips; substring matching is how false positives happen. The
///   one substring rule — contains "credit" — covers real variants ("End Credits &amp;
///   Outtakes") and is protected by a negative guard so post/mid-credits SCENE chapters
///   (content, not credits) never match. Because the FIRST credits match wins, a
///   post-credits scene chapter after the credits roll becomes the span end — skipping
///   credits lands exactly on the post-credits scene.
/// - Positional sanity guards reject semantically-matched but implausible chapters
///   (an "Ending" chapter at 40% of a film is content, not a credits roll).
/// </summary>
public static class ChapterMarkerMapper
{
    /// <summary>Intro chapters must start within the first third of the runtime…</summary>
    private const double IntroMaxStartFraction = 1.0 / 3.0;

    /// <summary>…and within the first 10 minutes regardless of runtime.</summary>
    private const double IntroMaxStartSeconds = 600.0;

    /// <summary>Credits chapters must start in the second half of the runtime.</summary>
    private const double CreditsMinStartFraction = 0.5;

    /// <summary>Spans shorter than this are chapter-authoring noise, not skippable segments.</summary>
    private const double MinSpanSeconds = 5.0;

    /// <summary>
    /// Span ceilings — REJECTION thresholds, not truncation. Live QA found a real file
    /// ("My Three Suns": "Opening Credits" at 54 s followed by "Scene 3" at 525 s — the
    /// authoring skipped a chapter) whose intro span came out at 471 s; a skip pill built
    /// from that jumps 8 minutes into the episode. Broken authoring must produce NO
    /// marker (detection then fills the gap), never an invented one. 5 min covers the
    /// longest legitimate opening-credit sequences; 15 min covers feature-film credits.
    /// </summary>
    private const double MaxIntroSpanSeconds = 300.0;
    private const double MaxCreditsSpanSeconds = 900.0;

    private static readonly HashSet<string> IntroTitles = new(StringComparer.Ordinal)
    {
        "intro", "opening", "opening credits", "opening titles", "opening theme",
        "main titles", "main title", "title sequence", "theme song", "op",
        "sigla", "sigla iniziale", // common in Italian releases
    };

    private static readonly HashSet<string> CreditsTitles = new(StringComparer.Ordinal)
    {
        "credits", "credit", "end credits", "closing credits", "final credits",
        "ending credits", "outro", "ending", "ed",
        "sigla finale", "titoli di coda", // common in Italian releases
    };

    /// <summary>
    /// Substrings that mark a credits-adjacent chapter as CONTENT (a scene, not the roll).
    /// Checked before the credits keyword rules.
    /// </summary>
    private static readonly string[] CreditsSceneMarkers = { "post", "mid", "after", "during" };

    /// <summary>
    /// Map an ordered chapter list onto intro/credits spans. <paramref name="chapters"/>
    /// must be sorted by start time (both call sites store/probe them that way; the mapper
    /// re-sorts defensively because correctness here is cheap). Returns
    /// <see cref="ChapterMarkerResult.Empty"/> when nothing matches — callers must then
    /// leave detection-owned values alone.
    /// </summary>
    public static ChapterMarkerResult Map(IReadOnlyList<(double StartTime, string Title)> chapters, double durationSeconds)
    {
        // Positional guards are meaningless without a real duration, and a single chapter
        // spanning the whole file carries no segment information.
        if (chapters == null || chapters.Count < 2 || durationSeconds <= 0)
            return ChapterMarkerResult.Empty;

        var ordered = chapters.OrderBy(c => c.StartTime).ToList();

        ChapterMarkerSpan? intro = null;
        ChapterMarkerSpan? credits = null;

        for (int i = 0; i < ordered.Count; i++)
        {
            var title = Normalize(ordered[i].Title);
            if (title.Length == 0) continue;

            var start = ordered[i].StartTime;
            var end = i + 1 < ordered.Count ? ordered[i + 1].StartTime : durationSeconds;
            if (end - start < MinSpanSeconds || start < 0 || end > durationSeconds + 1) continue;

            if (intro == null && IsIntroTitle(title) && IsPlausibleIntroPosition(start, durationSeconds)
                && end - start <= MaxIntroSpanSeconds)
            {
                // First plausible intro wins; i+1 must exist for a meaningful skip target,
                // which the Count-check above guarantees only for non-last chapters.
                if (i + 1 < ordered.Count)
                    intro = new ChapterMarkerSpan(start, end);
            }

            if (credits == null && IsCreditsTitle(title) && start >= durationSeconds * CreditsMinStartFraction
                && end - start <= MaxCreditsSpanSeconds)
            {
                // First credits match wins: a later "Post-Credits Scene" chapter then
                // bounds the span, so skipping credits lands on the scene, not past it.
                credits = new ChapterMarkerSpan(start, end);
            }

            if (intro != null && credits != null) break;
        }

        return new ChapterMarkerResult(intro, credits);
    }

    private static string Normalize(string? title) => (title ?? string.Empty).Trim().ToLowerInvariant();

    private static bool IsIntroTitle(string normalized) => IntroTitles.Contains(normalized);

    private static bool IsCreditsTitle(string normalized)
    {
        if (CreditsSceneMarkers.Any(normalized.Contains)) return false;
        return CreditsTitles.Contains(normalized) || normalized.Contains("credit");
    }

    private static bool IsPlausibleIntroPosition(double start, double duration) =>
        start <= Math.Min(duration * IntroMaxStartFraction, IntroMaxStartSeconds);
}
