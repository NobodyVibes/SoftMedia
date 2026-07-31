using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SoftMedia.Server.Data;
using SoftMedia.Server.Models;
using SoftMedia.Server.Services.Infrastructure;

namespace SoftMedia.Server.Services.Media.Detection;

/// <summary>
/// Default implementation of <see cref="IIntroCreditsDetectionService"/>. Uses a
/// pivot-episode strategy: episode 1 is the reference, every other episode is matched
/// pairwise against the pivot. Each non-pivot episode's matched range becomes its
/// own intro/credits timecodes; the pivot's own timecodes are derived from the
/// intersection of all pivot-side matches.
/// </summary>
public class IntroCreditsDetectionService : IIntroCreditsDetectionService
{
    // Window sizes for fingerprint extraction. 10 minutes covers cold-open + theme;
    // 6 minutes covers all but the longest end credits.
    private const double HeadWindowSeconds = 600.0;
    private const double TailWindowSeconds = 360.0;

    // Matcher tuning.
    //
    // MaxBitErrors = 8 (~75% bit-similarity threshold for extension). The
    // canonical Chromaprint default is 6 (~81%), which works for shows with
    // tight modern mastering (Arcane, prestige Netflix dramas) but fails on
    // older or inconsistently-encoded content (classic Futurama, syndicated
    // shows where DVD/streaming masters have drifted). At 6 bits, encoding
    // jitter can leave even the same audio failing to seed extensions for
    // most pairs. Loosening to 8 catches the typical case; the post-extension
    // trim at TrimMaxBitErrors=2 still prevents over-extension into non-intro
    // content, so this is asymmetric: more permissive about FINDING matches,
    // equally strict about where the boundary lands.
    //
    // MinSegmentSeconds = 10. Was 15 — too tight for Futurama-class shows
    // whose intros are ~22–25 s before the edge trim removes a few seconds
    // of noisy boundary, leaving a 12–14 s detected segment that we'd
    // previously reject. Studio logos (5–10 s) are still rejected. The
    // 5-minute IntroSearchEndSeconds cutoff and the universal MaxSegmentSeconds
    // cap remain unchanged — those are independent safeties.
    private const int MaxBitErrors = 8;
    private const double MinSegmentSeconds = 10.0;
    private const double MaxSegmentSeconds = 180.0;

    // Real TV intros always *start* within the first 5 minutes of the file.
    // Reject head-window matches whose episode-side start lands past this — those
    // are recurring background score or dialogue music, not theme music.
    private const double IntroSearchEndSeconds = 300.0;

    /// <summary>
    /// DV-WI-006: a duplicate copy of an episode within this duration delta of its
    /// representative shares the same cut — it inherits markers instead of being
    /// fingerprinted itself. Larger deltas (extended cuts) are fingerprinted normally.
    /// </summary>
    private const double DuplicateDurationToleranceSeconds = 2.0;

    private readonly AppDbContext _db;
    private readonly IFingerprintExtractor _extractor;
    private readonly ISegmentMatcher _matcher;
    private readonly ISettingsService _settings;
    private readonly ILogger<IntroCreditsDetectionService> _logger;

    public IntroCreditsDetectionService(
        AppDbContext db,
        IFingerprintExtractor extractor,
        ISegmentMatcher matcher,
        ISettingsService settings,
        ILogger<IntroCreditsDetectionService> logger)
    {
        _db = db;
        _extractor = extractor;
        _matcher = matcher;
        _settings = settings;
        _logger = logger;
    }

    public async Task<IntroCreditsDetectionResult> DetectAsync(Guid seriesId, CancellationToken cancellationToken = default)
    {
        var detectIntros = await _settings.GetSettingAsync("AutoDetectIntros", true);
        var detectCredits = await _settings.GetSettingAsync("AutoDetectCredits", true);

        if (!detectIntros && !detectCredits)
        {
            return new IntroCreditsDetectionResult(0, 0, 0, "Both intro and credits detection are disabled in settings.");
        }

        var episodes = await _db.MediaItems
            .Where(m => m.SeriesId == seriesId && m.Type == MediaType.Episode)
            .OrderBy(m => m.SeasonNumber).ThenBy(m => m.EpisodeNumber).ThenBy(m => m.Id)
            .ToListAsync(cancellationToken);

        // DV-WI-006: duplicate files of one episode share (Season, Episode). Fingerprint
        // ONE row per episode — matching two copies of the same content wastes ffmpeg
        // decode AND hands the all-pairs matcher a pair that "shares" its entire runtime.
        // A same-duration sibling inherits the representative's markers after detection
        // (through the source guards, so chapter-derived markers stay authoritative); a
        // sibling with a materially different duration (extended cut) stays in the pass
        // and is fingerprinted independently. Rows without a real episode number are
        // never grouped — several distinct unparseable files legitimately share E0.
        var working = new List<MediaItem>();
        var inheritors = new List<(MediaItem Duplicate, MediaItem Representative)>();
        working.AddRange(episodes.Where(e => !(e.EpisodeNumber is > 0)));
        foreach (var group in episodes.Where(e => e.EpisodeNumber is > 0)
                     .GroupBy(e => (e.SeasonNumber, e.EpisodeNumber)))
        {
            MediaItem? representative = null;
            foreach (var e in group)
            {
                if (representative == null)
                {
                    representative = e;
                    working.Add(e);
                }
                else if (Math.Abs(e.Duration - representative.Duration) <= DuplicateDurationToleranceSeconds)
                {
                    inheritors.Add((e, representative));
                }
                else
                {
                    working.Add(e);
                }
            }
        }

        if (working.Count < 2)
        {
            return new IntroCreditsDetectionResult(episodes.Count, 0, 0, "Need at least 2 episodes for cross-episode detection.");
        }

        // Load existing fingerprints in one query.
        var episodeIds = working.Select(e => e.Id).ToList();
        var fingerprints = await _db.MediaFingerprints
            .Where(f => episodeIds.Contains(f.MediaItemId))
            .ToDictionaryAsync(f => f.MediaItemId, cancellationToken);

        // Step 1: extract or reuse fingerprints for every episode. Episodes whose
        // extraction fails are skipped for matching but still get their
        // LastIntroDetectionUtc stamped so we don't retry on every scan.
        // Cancellation is checked per episode and each extracted fingerprint is
        // CHECKPOINTED immediately: when a scan preempts this job, completed episodes
        // stay persisted and the re-run resumes where it left off instead of
        // re-extracting the whole series.
        foreach (var episode in working)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var extractedNew = await EnsureFingerprintAsync(episode, fingerprints, detectIntros, detectCredits, cancellationToken);
            if (extractedNew)
            {
                await _db.SaveChangesAsync(cancellationToken);
            }
        }

        // Bail out cleanly if no episode produced any usable fingerprint at all
        // (typically means FFmpeg failed for the whole library — bad path, missing
        // binary, permissions, etc.). Stamp the attempt so we don't retry on every
        // scan and surface a clear reason.
        var anyUsableFingerprint = working.Any(e =>
            fingerprints.TryGetValue(e.Id, out var f)
            && ((detectIntros && f.HeadFingerprint != null) || (detectCredits && f.TailFingerprint != null)));

        if (!anyUsableFingerprint)
        {
            StampDetectionAttempt(episodes);
            await _db.SaveChangesAsync(cancellationToken);
            return new IntroCreditsDetectionResult(episodes.Count, 0, 0, "No episode has usable fingerprints.");
        }

        // Step 2: detect per-season. Different seasons of the same show often have
        // different intro arrangements (or completely different intros — Disenchantment
        // is a good example). Mixing seasons in one match pass causes the matcher to
        // either miss intros or land on a coincidentally-shared segment that isn't
        // either season's actual theme. Each season is detected independently.
        var seasonGroups = working
            .GroupBy(e => e.SeasonNumber ?? 0)
            .Where(g => g.Count() >= 2)
            .ToList();

        if (seasonGroups.Count == 0)
        {
            StampDetectionAttempt(episodes);
            await _db.SaveChangesAsync(cancellationToken);
            return new IntroCreditsDetectionResult(episodes.Count, 0, 0, "No season has at least 2 episodes for cross-episode detection.");
        }

        int introsFound = 0;
        int creditsFound = 0;

        foreach (var seasonGroup in seasonGroups)
        {
            var seasonNumber = seasonGroup.Key;
            var seasonEpisodes = seasonGroup.OrderBy(e => e.EpisodeNumber).ToList();

            var (intros, credits) = DetectForSeason(
                seasonNumber, seasonEpisodes, fingerprints, detectIntros, detectCredits, cancellationToken);

            introsFound += intros;
            creditsFound += credits;
        }

        // DV-WI-006: same-duration duplicates inherit their representative's markers.
        // TryWrite* enforces source precedence — a duplicate whose own file carries
        // chapter-derived markers keeps them.
        foreach (var (duplicate, representative) in inheritors)
        {
            if (representative.IntroStart.HasValue && representative.IntroEnd.HasValue
                && TryWriteIntro(duplicate, representative.IntroStart.Value, representative.IntroEnd.Value))
            {
                introsFound++;
            }
            if (representative.CreditsStart.HasValue
                && TryWriteCredits(duplicate, representative.CreditsStart.Value,
                       representative.CreditsEnd ?? representative.CreditsStart.Value))
            {
                creditsFound++;
            }
        }

        StampDetectionAttempt(episodes);
        await _db.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "[IntroCreditsDetection] Series {SeriesId}: {Eps} episodes across {Seasons} seasons, {Intros} intros, {Credits} credits",
            seriesId, episodes.Count, seasonGroups.Count, introsFound, creditsFound);

        return new IntroCreditsDetectionResult(episodes.Count, introsFound, creditsFound, null);
    }

    /// <summary>
    /// Detect intro/credits for a single season using all-pairs matching with
    /// per-episode median voting. Each pair of episodes in the season is matched
    /// once; for each episode we collect every position where the matcher landed
    /// the shared segment in this episode's fingerprint (from any pair this
    /// episode participated in). The median of those positions is this episode's
    /// intro/credits range.
    ///
    /// This replaces the single-pivot approach. With pivots, every other episode's
    /// detected position depended entirely on the pivot's specific intro instance —
    /// a quirky pivot poisoned the whole season. With all-pairs each episode gets
    /// independent observations from every other episode and the median averages
    /// out outlier pairs.
    ///
    /// Cost is O(N²) matches per season (N=10 → 45 pairs, N=50 → 1225). Matching
    /// is cheap relative to fingerprint extraction (which already happened before
    /// this method is called), so this is acceptable.
    /// </summary>
    private (int IntrosFound, int CreditsFound) DetectForSeason(
        int seasonNumber,
        List<MediaItem> seasonEpisodes,
        Dictionary<Guid, MediaFingerprint> fingerprints,
        bool detectIntros,
        bool detectCredits,
        CancellationToken cancellationToken)
    {
        if (seasonEpisodes.Count < 2) return (0, 0);

        var minLen = (int)Math.Ceiling(MinSegmentSeconds * _extractor.HashesPerSecond);
        var maxLen = (int)Math.Floor(MaxSegmentSeconds * _extractor.HashesPerSecond);

        // Step 1: run the matcher on every (i, j) pair, i < j. Each acceptable
        // match becomes a vote that contributes to BOTH episodes' position sets:
        // (AStart, AEnd) for ep i, (BStart, BEnd) for ep j.
        //
        // We track rejection categories so the season-level summary log surfaces
        // *why* detection failed when it does — null matches mean the matcher
        // can't seed (encoding too varied, raise MaxBitErrors), too-short means
        // the trim ate a real intro (lower MinSegmentSeconds), past-window means
        // the matched audio isn't actually an intro (working as intended).
        var introMatches = new List<PairMatch>();
        var creditsMatches = new List<PairMatch>();
        int introNullCount = 0, introTooShort = 0, introTooLong = 0, introPastWindow = 0;
        int creditsNullCount = 0, creditsTooShort = 0, creditsTooLong = 0;
        int introFingerprintMissing = 0, creditsFingerprintMissing = 0;

        int pairsConsidered = 0;
        for (int i = 0; i < seasonEpisodes.Count; i++)
        {
            for (int j = i + 1; j < seasonEpisodes.Count; j++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                pairsConsidered++;

                var ep1 = seasonEpisodes[i];
                var ep2 = seasonEpisodes[j];
                if (!fingerprints.TryGetValue(ep1.Id, out var fp1)) continue;
                if (!fingerprints.TryGetValue(ep2.Id, out var fp2)) continue;

                if (detectIntros)
                {
                    if (fp1.HeadFingerprint == null || fp2.HeadFingerprint == null)
                    {
                        introFingerprintMissing++;
                    }
                    else
                    {
                        var match = _matcher.FindLongestMatch(
                            HashesFrom(fp1.HeadFingerprint),
                            HashesFrom(fp2.HeadFingerprint),
                            minLen, MaxBitErrors);
                        ClassifyIntroMatch(match, maxLen,
                            ref introNullCount, ref introTooShort, ref introTooLong, ref introPastWindow,
                            accepted: m => introMatches.Add(new PairMatch(ep1, ep2, m)));
                    }
                }

                if (detectCredits)
                {
                    if (fp1.TailFingerprint == null || fp2.TailFingerprint == null)
                    {
                        creditsFingerprintMissing++;
                    }
                    else
                    {
                        var match = _matcher.FindLongestMatch(
                            HashesFrom(fp1.TailFingerprint),
                            HashesFrom(fp2.TailFingerprint),
                            minLen, MaxBitErrors);
                        ClassifyCreditsMatch(match, maxLen,
                            ref creditsNullCount, ref creditsTooShort, ref creditsTooLong,
                            accepted: m => creditsMatches.Add(new PairMatch(ep1, ep2, m)));
                    }
                }
            }
        }

        _logger.LogInformation(
            "[IntroCreditsDetection] S{Season}: matched {Pairs} pairs. " +
            "Intro: accepted={IntroOk} null={IntroNull} tooShort={IntroShort} tooLong={IntroLong} pastWindow={IntroPast} fpMissing={IntroFp}. " +
            "Credits: accepted={CreditsOk} null={CreditsNull} tooShort={CreditsShort} tooLong={CreditsLong} fpMissing={CreditsFp}.",
            seasonNumber, pairsConsidered,
            introMatches.Count, introNullCount, introTooShort, introTooLong, introPastWindow, introFingerprintMissing,
            creditsMatches.Count, creditsNullCount, creditsTooShort, creditsTooLong, creditsFingerprintMissing);

        // Step 2: for each episode, collect positions from every match it
        // participated in (on whichever side it appeared) and take the median as
        // its detected range. MinAnchors enforces consensus — episodes that only
        // appear in 1 match in a 10-episode season are probably noise.
        int introsFound = 0;
        int creditsFound = 0;
        int minAnchors = MinAnchorsFor(seasonEpisodes.Count);

        foreach (var episode in seasonEpisodes)
        {
            var introPositions = CollectPositionsFor(episode, introMatches);
            if (introPositions.Count >= minAnchors)
            {
                var (medianStart, medianEnd) = MedianRange(introPositions);
                var (startSec, endSec) = HeadIndicesToSeconds(medianStart, medianEnd);
                if (TryWriteIntro(episode, startSec, endSec))
                {
                    introsFound++;
                    _logger.LogInformation(
                        "[IntroCreditsDetection] Intro detected for S{Season}E{Episode} '{Title}': {Start:F1}s → {End:F1}s ({Length:F1}s, {Anchors} anchors)",
                        episode.SeasonNumber, episode.EpisodeNumber, episode.Title,
                        startSec, endSec, endSec - startSec, introPositions.Count);
                }
            }
            else if (introPositions.Count > 0)
            {
                _logger.LogInformation(
                    "[IntroCreditsDetection] No intro consensus for S{Season}E{Episode} '{Title}': only {Have} anchors (need {Need})",
                    episode.SeasonNumber, episode.EpisodeNumber, episode.Title,
                    introPositions.Count, minAnchors);
            }

            var creditsPositions = CollectPositionsFor(episode, creditsMatches);
            if (creditsPositions.Count >= minAnchors)
            {
                var (medianStart, medianEnd) = MedianRange(creditsPositions);
                var fp = fingerprints[episode.Id];
                var (startSec, endSec) = TailIndicesToSeconds(medianStart, medianEnd, episode.Duration, fp.TailDurationSeconds);
                if (TryWriteCredits(episode, startSec, endSec))
                {
                    creditsFound++;
                    _logger.LogInformation(
                        "[IntroCreditsDetection] Credits detected for S{Season}E{Episode} '{Title}': {Start:F1}s → {End:F1}s ({Length:F1}s, {Anchors} anchors)",
                        episode.SeasonNumber, episode.EpisodeNumber, episode.Title,
                        startSec, endSec, endSec - startSec, creditsPositions.Count);
                }
            }
        }

        return (introsFound, creditsFound);
    }

    /// <summary>
    /// Minimum number of pairwise matches an episode must participate in for its
    /// detected range to be considered reliable. Scales with season size — bigger
    /// seasons should have stronger consensus before we trust a result.
    /// </summary>
    private static int MinAnchorsFor(int seasonSize)
    {
        // 2 episodes can only produce 1 pair; require it.
        if (seasonSize == 2) return 1;
        // 3-4 episodes: require 2 anchors.
        // 5+ episodes: require 3 anchors. Above 5 the threshold doesn't grow
        // — we'd rather over-detect on big seasons than miss episodes whose
        // intros happen to match poorly with a few bad pairs.
        return seasonSize <= 4 ? 2 : 3;
    }

    /// <summary>
    /// Pull every position where this episode's fingerprint produced a match,
    /// regardless of which side of the pair it appeared on. AStart/AEnd if it was
    /// the first episode in the pair; BStart/BEnd if it was the second.
    /// </summary>
    private static List<(int Start, int End)> CollectPositionsFor(MediaItem episode, List<PairMatch> matches)
    {
        var positions = new List<(int Start, int End)>();
        foreach (var pm in matches)
        {
            if (pm.Ep1.Id == episode.Id)
                positions.Add((pm.Match.AStart, pm.Match.AEnd));
            else if (pm.Ep2.Id == episode.Id)
                positions.Add((pm.Match.BStart, pm.Match.BEnd));
        }
        return positions;
    }

    /// <summary>
    /// Median of position ranges, computed independently for start and end.
    /// Computing them independently rather than as paired tuples keeps the central
    /// cluster of values regardless of which match contributes which.
    /// </summary>
    private static (int Start, int End) MedianRange(List<(int Start, int End)> positions)
    {
        var starts = positions.Select(p => p.Start).OrderBy(v => v).ToList();
        var ends = positions.Select(p => p.End).OrderBy(v => v).ToList();
        return (starts[starts.Count / 2], ends[ends.Count / 2]);
    }

    /// <summary>
    /// One pairwise match: the two episodes that produced it and the segment
    /// the matcher returned. AStart/AEnd are positions in Ep1, BStart/BEnd in Ep2.
    /// </summary>
    private record PairMatch(MediaItem Ep1, MediaItem Ep2, SegmentMatch Match);

    /// <summary>
    /// Bucket an intro-side match into accepted / null / too short / too long /
    /// past-window. Used for diagnostic logging so we can tell at a glance why a
    /// season produced no detections.
    /// </summary>
    private void ClassifyIntroMatch(
        SegmentMatch? match, int maxLen,
        ref int nullCount, ref int tooShort, ref int tooLong, ref int pastWindow,
        Action<SegmentMatch> accepted)
    {
        if (match == null) { nullCount++; return; }
        if (match.Length > maxLen) { tooLong++; return; }

        var hz = _extractor.HashesPerSecond;
        if (match.AStart / hz > IntroSearchEndSeconds || match.BStart / hz > IntroSearchEndSeconds)
        {
            pastWindow++;
            return;
        }
        // The matcher already enforces minLen at find-time, but trim can shrink
        // a returned match below it. Belt-and-suspenders length check here so
        // post-trim shrinkage shows up as a tooShort rejection rather than
        // silently producing a noisy short range.
        var minLen = (int)Math.Ceiling(MinSegmentSeconds * hz);
        if (match.Length < minLen) { tooShort++; return; }

        accepted(match);
    }

    /// <summary>
    /// Same as <see cref="ClassifyIntroMatch"/> but without the head-window
    /// position check — credits are intrinsically tail-window so position
    /// validation isn't applicable.
    /// </summary>
    private void ClassifyCreditsMatch(
        SegmentMatch? match, int maxLen,
        ref int nullCount, ref int tooShort, ref int tooLong,
        Action<SegmentMatch> accepted)
    {
        if (match == null) { nullCount++; return; }
        if (match.Length > maxLen) { tooLong++; return; }

        var minLen = (int)Math.Ceiling(MinSegmentSeconds * _extractor.HashesPerSecond);
        if (match.Length < minLen) { tooShort++; return; }

        accepted(match);
    }

    /// <summary>Returns true when new fingerprint data was extracted (caller checkpoints it).</summary>
    private async Task<bool> EnsureFingerprintAsync(
        MediaItem episode,
        Dictionary<Guid, MediaFingerprint> fingerprints,
        bool extractHead,
        bool extractTail,
        CancellationToken ct)
    {
        if (!fingerprints.TryGetValue(episode.Id, out var fp))
        {
            fp = new MediaFingerprint { MediaItemId = episode.Id };
            _db.MediaFingerprints.Add(fp);
            fingerprints[episode.Id] = fp;
        }

        var extractedNew = false;

        if (extractHead && fp.HeadFingerprint == null)
        {
            var hashes = await _extractor.ExtractHeadAsync(episode.Path, HeadWindowSeconds, ct);
            if (hashes != null && hashes.Length > 0)
            {
                fp.HeadFingerprint = HashesToBytes(hashes);
                fp.HeadDurationSeconds = HeadWindowSeconds;
                fp.GeneratedUtc = DateTime.UtcNow;
                extractedNew = true;
            }
        }

        if (extractTail && fp.TailFingerprint == null)
        {
            var hashes = await _extractor.ExtractTailAsync(episode.Path, TailWindowSeconds, ct);
            if (hashes != null && hashes.Length > 0)
            {
                fp.TailFingerprint = HashesToBytes(hashes);
                fp.TailDurationSeconds = TailWindowSeconds;
                fp.GeneratedUtc = DateTime.UtcNow;
                extractedNew = true;
            }
        }

        return extractedNew;
    }

    private bool IsAcceptable(SegmentMatch? match, int maxLen)
    {
        return match != null && match.Length <= maxLen;
    }

    /// <summary>
    /// Stricter acceptance for head (intro) matches: BOTH episodes' starts must be
    /// within the typical intro window. With all-pairs matching the A-side and
    /// B-side are both episode positions (no pivot bias), so a match that places
    /// one episode's intro at minute 7 is recurring score, not theme music — even
    /// if the other episode's position happens to look like an intro.
    /// </summary>
    private bool IsAcceptableIntroMatch(SegmentMatch? match, int maxLen)
    {
        if (!IsAcceptable(match, maxLen)) return false;
        var hz = _extractor.HashesPerSecond;
        return match!.AStart / hz <= IntroSearchEndSeconds
            && match.BStart / hz <= IntroSearchEndSeconds;
    }

    private bool TryWriteIntro(MediaItem episode, double start, double end)
    {
        // Source precedence: never overwrite a chapter-derived value with detection.
        if (episode.IntroSource == DetectionSource.Chapter) return false;
        episode.IntroStart = start;
        episode.IntroEnd = end;
        episode.IntroSource = DetectionSource.Detected;
        return true;
    }

    private bool TryWriteCredits(MediaItem episode, double start, double end)
    {
        if (episode.CreditsSource == DetectionSource.Chapter) return false;
        episode.CreditsStart = start;
        episode.CreditsEnd = end;
        episode.CreditsSource = DetectionSource.Detected;
        return true;
    }

    private static bool HasChapterIntro(MediaItem episode)
        => episode.IntroSource == DetectionSource.Chapter && episode.IntroStart.HasValue;

    private static bool HasChapterCredits(MediaItem episode)
        => episode.CreditsSource == DetectionSource.Chapter && episode.CreditsStart.HasValue;

    /// <summary>
    /// Translate inclusive fingerprint indices into [start, end) seconds in the
    /// head window. Index 0 of the head fingerprint is t=0 of the file.
    /// </summary>
    private (double Start, double End) HeadIndicesToSeconds(int startIndex, int endIndex)
    {
        var hz = _extractor.HashesPerSecond;
        return (startIndex / hz, (endIndex + 1) / hz);
    }

    /// <summary>
    /// Translate inclusive fingerprint indices into [start, end) seconds in the
    /// tail window. Index 0 of the tail fingerprint corresponds to
    /// (duration - tailWindow) seconds from start of file.
    /// </summary>
    private (double Start, double End) TailIndicesToSeconds(int startIndex, int endIndex, double episodeDuration, double tailWindow)
    {
        var hz = _extractor.HashesPerSecond;
        var tailStartSeconds = Math.Max(0, episodeDuration - tailWindow);
        return (tailStartSeconds + (startIndex / hz), tailStartSeconds + ((endIndex + 1) / hz));
    }

    private static void StampDetectionAttempt(List<MediaItem> episodes)
    {
        var now = DateTime.UtcNow;
        foreach (var episode in episodes)
        {
            episode.LastIntroDetectionUtc = now;
        }
    }

    private static byte[] HashesToBytes(uint[] hashes)
    {
        var bytes = new byte[hashes.Length * 4];
        for (int i = 0; i < hashes.Length; i++)
        {
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(i * 4, 4), hashes[i]);
        }
        return bytes;
    }

    private static uint[] HashesFrom(byte[] bytes)
    {
        var count = bytes.Length / 4;
        var hashes = new uint[count];
        for (int i = 0; i < count; i++)
        {
            hashes[i] = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(i * 4, 4));
        }
        return hashes;
    }
}
