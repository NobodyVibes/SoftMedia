using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SoftMedia.Server.Data;
using SoftMedia.Server.Models;
using SoftMedia.Server.Services.Infrastructure;
using SoftMedia.Server.Services.Media.Detection;
using Xunit;

namespace SoftMedia.Server.Tests.Services.Media.Detection;

/// <summary>
/// Orchestration tests for IntroCreditsDetectionService. The fingerprint extractor
/// and segment matcher are mocked so these tests stay deterministic and FFmpeg-free.
/// We're verifying:
///   - chapter-derived values are never overwritten,
///   - extracted fingerprints are persisted,
///   - per-episode + pivot timecodes are written,
///   - LastIntroDetectionUtc is stamped even on no-match runs,
///   - series with <2 episodes short-circuit cleanly.
/// </summary>
public class IntroCreditsDetectionServiceTests
{
    private readonly Mock<IFingerprintExtractor> _extractor = new();
    private readonly Mock<ISegmentMatcher> _matcher = new();
    private readonly Mock<ISettingsService> _settings = new();

    public IntroCreditsDetectionServiceTests()
    {
        // Pin the hash rate so seconds-conversion is deterministic across tests.
        // 10 hashes/sec → index 50 = 5 seconds, index 200 = 20 seconds, etc.
        _extractor.SetupGet(e => e.HashesPerSecond).Returns(10.0);

        // Default: both detection passes enabled. Individual tests override as needed.
        _settings.Setup(s => s.GetSettingAsync("AutoDetectIntros", true)).ReturnsAsync(true);
        _settings.Setup(s => s.GetSettingAsync("AutoDetectCredits", true)).ReturnsAsync(true);
    }

    [Fact]
    public async Task DetectAsync_ShortCircuits_WhenBothDetectionPassesAreDisabled()
    {
        await using var db = NewDb();
        var series = AddSeriesWithEpisodes(db, episodeCount: 3, episodeDuration: 1800);

        _settings.Setup(s => s.GetSettingAsync("AutoDetectIntros", true)).ReturnsAsync(false);
        _settings.Setup(s => s.GetSettingAsync("AutoDetectCredits", true)).ReturnsAsync(false);

        var service = NewService(db);

        var result = await service.DetectAsync(series.Id);

        Assert.NotNull(result.FailureReason);
        Assert.Equal(0, result.EpisodesProcessed);
        _extractor.Verify(e => e.ExtractHeadAsync(It.IsAny<string>(), It.IsAny<double>(), It.IsAny<CancellationToken>()), Times.Never);
        _extractor.Verify(e => e.ExtractTailAsync(It.IsAny<string>(), It.IsAny<double>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task DetectAsync_ReturnsFailureReason_WhenSeriesHasFewerThanTwoEpisodes()
    {
        await using var db = NewDb();
        var series = AddSeriesWithEpisodes(db, episodeCount: 1, episodeDuration: 1800);

        var service = NewService(db);

        var result = await service.DetectAsync(series.Id);

        Assert.NotNull(result.FailureReason);
        Assert.Equal(1, result.EpisodesProcessed);
        Assert.Equal(0, result.IntrosFound);
        _extractor.Verify(e => e.ExtractHeadAsync(It.IsAny<string>(), It.IsAny<double>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task DetectAsync_Cancellation_Propagates_AndKeepsCheckpointedFingerprints()
    {
        await using var db = NewDb();
        var series = AddSeriesWithEpisodes(db, episodeCount: 3, episodeDuration: 1800);

        using var cts = new CancellationTokenSource();
        int headCalls = 0;
        // Episode 2's extraction is where cancellation lands (the fixed extractor
        // propagates it as OCE instead of swallowing it).
        _extractor.Setup(e => e.ExtractHeadAsync(It.IsAny<string>(), It.IsAny<double>(), It.IsAny<CancellationToken>()))
            .Returns((string _, double _, CancellationToken _) =>
            {
                if (Interlocked.Increment(ref headCalls) == 2)
                {
                    cts.Cancel();
                    throw new OperationCanceledException(cts.Token);
                }
                return Task.FromResult<uint[]?>(BuildFingerprint(length: 1000));
            });
        _extractor.Setup(e => e.ExtractTailAsync(It.IsAny<string>(), It.IsAny<double>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(BuildFingerprint(length: 1000));

        var service = NewService(db);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.DetectAsync(series.Id, cts.Token));

        // Episode 1's fingerprint was checkpointed before cancellation, so a re-run
        // (after preemption) resumes instead of starting over; episode 3 never started.
        var stored = await db.MediaFingerprints.ToListAsync();
        Assert.Single(stored);
        Assert.NotNull(stored[0].HeadFingerprint);
        Assert.Equal(2, headCalls);
    }

    [Fact]
    public async Task DetectAsync_PersistsFingerprintsForAllEpisodes()
    {
        await using var db = NewDb();
        var series = AddSeriesWithEpisodes(db, episodeCount: 3, episodeDuration: 1800);

        // Same fingerprints for every call so the matcher has something to match on.
        _extractor.Setup(e => e.ExtractHeadAsync(It.IsAny<string>(), It.IsAny<double>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(BuildFingerprint(length: 1000));
        _extractor.Setup(e => e.ExtractTailAsync(It.IsAny<string>(), It.IsAny<double>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(BuildFingerprint(length: 1000));
        _matcher.Setup(m => m.FindLongestMatch(It.IsAny<uint[]>(), It.IsAny<uint[]>(), It.IsAny<int>(), It.IsAny<int>()))
            .Returns((SegmentMatch?)null);

        var service = NewService(db);
        await service.DetectAsync(series.Id);

        var stored = await db.MediaFingerprints.ToListAsync();
        Assert.Equal(3, stored.Count);
        Assert.All(stored, f =>
        {
            Assert.NotNull(f.HeadFingerprint);
            Assert.NotNull(f.TailFingerprint);
        });
    }

    [Fact]
    public async Task DetectAsync_WritesIntroAndCreditsTimecodes_WhenMatcherReturnsRange()
    {
        // All-pairs algorithm: with 2 episodes there is exactly 1 pair (E01, E02).
        // The matcher's returned SegmentMatch carries A-side indices (E01) and
        // B-side indices (E02). Both episodes get their own positions written —
        // there is no "pivot" or "other" distinction.
        await using var db = NewDb();
        var series = AddSeriesWithEpisodes(db, episodeCount: 2, episodeDuration: 1800);
        var ep1Id = db.MediaItems.First(m => m.Type == MediaType.Episode && m.SeriesId == series.Id && m.EpisodeNumber == 1).Id;
        var ep2Id = db.MediaItems.First(m => m.Type == MediaType.Episode && m.SeriesId == series.Id && m.EpisodeNumber == 2).Id;

        _extractor.Setup(e => e.ExtractHeadAsync(It.IsAny<string>(), It.IsAny<double>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(BuildFingerprint(length: 6000));
        _extractor.Setup(e => e.ExtractTailAsync(It.IsAny<string>(), It.IsAny<double>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(BuildFingerprint(length: 3600));

        // Intro: A-side (E01) indices 120..420 → 12.0s..42.1s
        //        B-side (E02) indices 100..400 → 10.0s..40.1s
        // Credits: tail-start = 1800-360 = 1440s
        //          A-side (E01) indices 220..520 → 1462.0s..1492.1s
        //          B-side (E02) indices 200..500 → 1460.0s..1490.1s
        _matcher.SetupSequence(m => m.FindLongestMatch(It.IsAny<uint[]>(), It.IsAny<uint[]>(), It.IsAny<int>(), It.IsAny<int>()))
            .Returns(new SegmentMatch(120, 420, 100, 400))
            .Returns(new SegmentMatch(220, 520, 200, 500));

        var service = NewService(db);
        var result = await service.DetectAsync(series.Id);

        Assert.Null(result.FailureReason);

        var ep1 = await db.MediaItems.FirstAsync(m => m.Id == ep1Id);
        Assert.Equal(DetectionSource.Detected, ep1.IntroSource);
        Assert.Equal(12.0, ep1.IntroStart!.Value, precision: 1);
        Assert.Equal(42.1, ep1.IntroEnd!.Value, precision: 1);
        Assert.Equal(DetectionSource.Detected, ep1.CreditsSource);
        Assert.Equal(1462.0, ep1.CreditsStart!.Value, precision: 1);
        Assert.Equal(1492.1, ep1.CreditsEnd!.Value, precision: 1);

        var ep2 = await db.MediaItems.FirstAsync(m => m.Id == ep2Id);
        Assert.Equal(DetectionSource.Detected, ep2.IntroSource);
        Assert.Equal(10.0, ep2.IntroStart!.Value, precision: 1);
        Assert.Equal(40.1, ep2.IntroEnd!.Value, precision: 1);
        Assert.Equal(DetectionSource.Detected, ep2.CreditsSource);
        Assert.Equal(1460.0, ep2.CreditsStart!.Value, precision: 1);
        Assert.Equal(1490.1, ep2.CreditsEnd!.Value, precision: 1);
    }

    [Fact]
    public async Task DetectAsync_RunsDetectionPerSeasonIndependently()
    {
        // A 4-episode series split across 2 seasons (S01: E01,E02; S02: E01,E02).
        // Each season should run its own detection pass with its own pivot — this
        // pins the per-season grouping that lets shows with different intros across
        // seasons (e.g. Disenchantment) detect correctly.
        await using var db = NewDb();
        var libraryId = Guid.NewGuid();
        var series = new MediaItem
        {
            Id = Guid.NewGuid(),
            LibraryId = libraryId,
            Title = "Multi-season Show",
            Path = "/series",
            Type = MediaType.Series
        };
        db.MediaItems.Add(series);

        void AddEp(int season, int episode) => db.MediaItems.Add(new MediaItem
        {
            Id = Guid.NewGuid(),
            LibraryId = libraryId,
            Title = $"S{season:00}E{episode:00}",
            Path = $"/s{season}e{episode}.mkv",
            Type = MediaType.Episode,
            SeriesId = series.Id,
            SeasonNumber = season,
            EpisodeNumber = episode,
            Duration = 1800
        });

        AddEp(1, 1); AddEp(1, 2);
        AddEp(2, 1); AddEp(2, 2);
        db.SaveChanges();

        _extractor.Setup(e => e.ExtractHeadAsync(It.IsAny<string>(), It.IsAny<double>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(BuildFingerprint(length: 6000));
        _extractor.Setup(e => e.ExtractTailAsync(It.IsAny<string>(), It.IsAny<double>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(BuildFingerprint(length: 3600));

        // Each season has 2 episodes → SelectPivotIndices(2) = [1] → pivot = E02 of
        // that season → 1 non-pivot pairing → 1 intro call + 1 credits call. Across
        // two seasons that's 4 matcher calls. Distinct ranges per season prove the
        // seasons were detected independently.
        _matcher.SetupSequence(m => m.FindLongestMatch(It.IsAny<uint[]>(), It.IsAny<uint[]>(), It.IsAny<int>(), It.IsAny<int>()))
            .Returns(new SegmentMatch(120, 420, 100, 400))   // S01 intro: BStart=100 → 10.0s
            .Returns(new SegmentMatch(220, 520, 200, 500))   // S01 credits: BStart=200 → 1460.0s
            .Returns(new SegmentMatch(150, 450, 130, 430))   // S02 intro: BStart=130 → 13.0s
            .Returns(new SegmentMatch(250, 550, 230, 530));  // S02 credits: BStart=230 → 1463.0s

        var service = NewService(db);
        var result = await service.DetectAsync(series.Id);

        Assert.Null(result.FailureReason);

        // Season 1's pair gives S01E01 the A-side (120..420 → 12.0..42.1s) and
        // S01E02 the B-side (100..400 → 10.0..40.1s). Season 2's pair uses
        // distinct values (150..450, 130..430) so we can prove the seasons were
        // matched independently.
        var s1ep1 = await db.MediaItems.FirstAsync(m => m.SeasonNumber == 1 && m.EpisodeNumber == 1);
        var s1ep2 = await db.MediaItems.FirstAsync(m => m.SeasonNumber == 1 && m.EpisodeNumber == 2);
        var s2ep1 = await db.MediaItems.FirstAsync(m => m.SeasonNumber == 2 && m.EpisodeNumber == 1);
        var s2ep2 = await db.MediaItems.FirstAsync(m => m.SeasonNumber == 2 && m.EpisodeNumber == 2);

        Assert.Equal(12.0, s1ep1.IntroStart!.Value, precision: 1);
        Assert.Equal(42.1, s1ep1.IntroEnd!.Value, precision: 1);
        Assert.Equal(10.0, s1ep2.IntroStart!.Value, precision: 1);
        Assert.Equal(40.1, s1ep2.IntroEnd!.Value, precision: 1);

        Assert.Equal(15.0, s2ep1.IntroStart!.Value, precision: 1);
        Assert.Equal(45.1, s2ep1.IntroEnd!.Value, precision: 1);
        Assert.Equal(13.0, s2ep2.IntroStart!.Value, precision: 1);
        Assert.Equal(43.1, s2ep2.IntroEnd!.Value, precision: 1);
    }

    [Fact]
    public async Task DetectAsync_AllPairsClusterVoting_FiltersOutlierMatch()
    {
        // 5 episodes in S01 → 10 pairs. The matcher returns a consistent intro
        // position for 9 of them and an outlier for the very first call (the
        // (E01, E02) pair). With minAnchors=3 and 4 anchors per episode, the
        // median should ignore the outlier and write the consistent position to
        // every episode — including E01 and E02 which were the ones in the
        // outlier pair. This pins the behavior that single bad matches no longer
        // contaminate detection (the failure mode of the old single-pivot code).
        await using var db = NewDb();
        var series = AddSeriesWithEpisodes(db, episodeCount: 5, episodeDuration: 1800);

        _extractor.Setup(e => e.ExtractHeadAsync(It.IsAny<string>(), It.IsAny<double>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(BuildFingerprint(length: 6000));
        _extractor.Setup(e => e.ExtractTailAsync(It.IsAny<string>(), It.IsAny<double>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(BuildFingerprint(length: 3600));

        // Pair iteration: (E1,E2), (E1,E3), (E1,E4), (E1,E5), (E2,E3), (E2,E4),
        //                 (E2,E5), (E3,E4), (E3,E5), (E4,E5) — 10 pairs.
        // Each pair: intro call, then credits call. 20 returns total.
        // First pair (E1,E2): outlier in both intro and credits.
        // Remaining 9 pairs: consistent values.
        var consistentIntro = new SegmentMatch(100, 400, 100, 400);   // → 10.0s..40.1s
        var outlierIntro = new SegmentMatch(800, 1100, 800, 1100);    // → 80.0s..110.1s
        var consistentCredits = new SegmentMatch(150, 450, 150, 450); // → 1455.0s..1485.1s
        var outlierCredits = new SegmentMatch(900, 1200, 900, 1200);  // → 1530.0s..1560.1s

        var seq = _matcher.SetupSequence(m => m.FindLongestMatch(It.IsAny<uint[]>(), It.IsAny<uint[]>(), It.IsAny<int>(), It.IsAny<int>()))
            .Returns(outlierIntro)
            .Returns(outlierCredits);
        for (int i = 0; i < 9; i++)
        {
            seq = seq.Returns(consistentIntro).Returns(consistentCredits);
        }

        var service = NewService(db);
        var result = await service.DetectAsync(series.Id);

        Assert.Null(result.FailureReason);

        var episodes = await db.MediaItems
            .Where(m => m.Type == MediaType.Episode)
            .OrderBy(m => m.EpisodeNumber)
            .ToListAsync();

        // All 5 episodes should land at the consistent values, including E01/E02
        // whose anchor lists each contain one outlier observation that the
        // median votes out.
        Assert.All(episodes, ep =>
        {
            Assert.Equal(DetectionSource.Detected, ep.IntroSource);
            Assert.Equal(10.0, ep.IntroStart!.Value, precision: 1);
            Assert.Equal(40.1, ep.IntroEnd!.Value, precision: 1);

            Assert.Equal(DetectionSource.Detected, ep.CreditsSource);
            Assert.Equal(1455.0, ep.CreditsStart!.Value, precision: 1);
            Assert.Equal(1485.1, ep.CreditsEnd!.Value, precision: 1);
        });
    }

    [Fact]
    public async Task DetectAsync_RejectsIntroMatchesThatStartPastTheFiveMinuteWindow()
    {
        // Real TV intros start within the first 5 minutes. A match whose episode-side
        // start lands at, say, 7 minutes into the file is recurring background score
        // or dialogue music, not theme music. Detection must reject it instead of
        // writing a wrong IntroStart/End to the DB.
        await using var db = NewDb();
        var series = AddSeriesWithEpisodes(db, episodeCount: 2, episodeDuration: 1800);

        _extractor.Setup(e => e.ExtractHeadAsync(It.IsAny<string>(), It.IsAny<double>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(BuildFingerprint(length: 6000));
        _extractor.Setup(e => e.ExtractTailAsync(It.IsAny<string>(), It.IsAny<double>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(BuildFingerprint(length: 3600));

        // Match with BStart at index 4200 = 420s = 7 minutes. Past the 5-min bound.
        _matcher.SetupSequence(m => m.FindLongestMatch(It.IsAny<uint[]>(), It.IsAny<uint[]>(), It.IsAny<int>(), It.IsAny<int>()))
            .Returns(new SegmentMatch(4200, 4500, 4200, 4500))  // intro — must reject
            .Returns((SegmentMatch?)null);                       // credits — no match

        var service = NewService(db);
        var result = await service.DetectAsync(series.Id);

        Assert.Null(result.FailureReason);
        Assert.Equal(0, result.IntrosFound);
        var episodes = await db.MediaItems.Where(m => m.Type == MediaType.Episode).ToListAsync();
        Assert.All(episodes, e =>
        {
            Assert.Null(e.IntroStart);
            Assert.Null(e.IntroEnd);
            Assert.Null(e.IntroSource);
        });
    }

    [Fact]
    public async Task DetectAsync_NeverOverwritesChapterDerivedTimecodes()
    {
        await using var db = NewDb();
        var series = AddSeriesWithEpisodes(db, episodeCount: 2, episodeDuration: 1800);

        // Mark the second episode's intro as Chapter-derived with sentinel values.
        var ep2 = await db.MediaItems.FirstAsync(m => m.Type == MediaType.Episode && m.EpisodeNumber == 2);
        ep2.IntroSource = DetectionSource.Chapter;
        ep2.IntroStart = 5.0;
        ep2.IntroEnd = 35.0;
        await db.SaveChangesAsync();

        _extractor.Setup(e => e.ExtractHeadAsync(It.IsAny<string>(), It.IsAny<double>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(BuildFingerprint(length: 6000));
        _extractor.Setup(e => e.ExtractTailAsync(It.IsAny<string>(), It.IsAny<double>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(BuildFingerprint(length: 3600));
        // Always claim a match.
        _matcher.Setup(m => m.FindLongestMatch(It.IsAny<uint[]>(), It.IsAny<uint[]>(), It.IsAny<int>(), It.IsAny<int>()))
            .Returns(new SegmentMatch(900, 1200, 900, 1200));

        var service = NewService(db);
        await service.DetectAsync(series.Id);

        var refreshed = await db.MediaItems.FirstAsync(m => m.Id == ep2.Id);
        Assert.Equal(DetectionSource.Chapter, refreshed.IntroSource);
        Assert.Equal(5.0, refreshed.IntroStart!.Value);
        Assert.Equal(35.0, refreshed.IntroEnd!.Value);
    }

    [Fact]
    public async Task DetectAsync_StampsLastDetectionUtc_EvenWhenNoMatchFound()
    {
        await using var db = NewDb();
        var series = AddSeriesWithEpisodes(db, episodeCount: 2, episodeDuration: 1800);
        var beforeRun = DateTime.UtcNow.AddSeconds(-1);

        _extractor.Setup(e => e.ExtractHeadAsync(It.IsAny<string>(), It.IsAny<double>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(BuildFingerprint(length: 6000));
        _extractor.Setup(e => e.ExtractTailAsync(It.IsAny<string>(), It.IsAny<double>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(BuildFingerprint(length: 3600));
        _matcher.Setup(m => m.FindLongestMatch(It.IsAny<uint[]>(), It.IsAny<uint[]>(), It.IsAny<int>(), It.IsAny<int>()))
            .Returns((SegmentMatch?)null);

        var service = NewService(db);
        await service.DetectAsync(series.Id);

        var episodes = await db.MediaItems.Where(m => m.Type == MediaType.Episode).ToListAsync();
        Assert.All(episodes, e =>
        {
            Assert.NotNull(e.LastIntroDetectionUtc);
            Assert.True(e.LastIntroDetectionUtc!.Value >= beforeRun);
        });
    }

    [Fact]
    public async Task DetectAsync_FailsCleanly_WhenAllExtractionsFail()
    {
        await using var db = NewDb();
        var series = AddSeriesWithEpisodes(db, episodeCount: 2, episodeDuration: 1800);

        _extractor.Setup(e => e.ExtractHeadAsync(It.IsAny<string>(), It.IsAny<double>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((uint[]?)null);
        _extractor.Setup(e => e.ExtractTailAsync(It.IsAny<string>(), It.IsAny<double>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((uint[]?)null);

        var service = NewService(db);
        var result = await service.DetectAsync(series.Id);

        Assert.NotNull(result.FailureReason);
        Assert.Equal(0, result.IntrosFound);
        Assert.Equal(0, result.CreditsFound);

        // Even on extraction failure, attempt timestamp must land so we don't retry on every scan.
        var episodes = await db.MediaItems.Where(m => m.Type == MediaType.Episode).ToListAsync();
        Assert.All(episodes, e => Assert.NotNull(e.LastIntroDetectionUtc));
    }

    // ──────────────── DV-WI-006: duplicate files of the same episode ────────────────

    private static MediaItem AddDuplicateEpisode(AppDbContext db, MediaItem series, int season, int episode, double duration, Guid id)
    {
        var dup = new MediaItem
        {
            Id = id,
            LibraryId = series.LibraryId,
            Title = $"S{season:00}E{episode:00} (copy)",
            Path = $"/s{season}e{episode}-copy.mkv",
            Type = MediaType.Episode,
            SeriesId = series.Id,
            SeasonNumber = season,
            EpisodeNumber = episode,
            Duration = duration
        };
        db.MediaItems.Add(dup);
        db.SaveChanges();
        return dup;
    }

    [Fact]
    public async Task DetectAsync_FingerprintsDuplicateEpisodeOnce_AndDuplicateInheritsMarkers()
    {
        // Two files of S01E02 (same cut — same duration). Only the representative (lower
        // id) is fingerprinted; the duplicate inherits its detected markers afterwards.
        await using var db = NewDb();
        var series = AddSeriesWithEpisodes(db, episodeCount: 2, episodeDuration: 1800);
        var ep2 = db.MediaItems.First(m => m.Type == MediaType.Episode && m.EpisodeNumber == 2);
        // The duplicate's near-max GUID pins it AFTER ep2 in (Season, Episode, Id) order,
        // so ep2 is deterministically the representative.
        var dup = AddDuplicateEpisode(db, db.MediaItems.First(m => m.Id == series.Id), 1, 2, 1800,
            Guid.Parse("ffffffff-ffff-ffff-ffff-fffffffffffe"));

        _extractor.Setup(e => e.ExtractHeadAsync(It.IsAny<string>(), It.IsAny<double>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(BuildFingerprint(length: 6000));
        _extractor.Setup(e => e.ExtractTailAsync(It.IsAny<string>(), It.IsAny<double>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(BuildFingerprint(length: 3600));
        _matcher.SetupSequence(m => m.FindLongestMatch(It.IsAny<uint[]>(), It.IsAny<uint[]>(), It.IsAny<int>(), It.IsAny<int>()))
            .Returns(new SegmentMatch(120, 420, 100, 400))   // intro: B-side (E02) 10.0s..40.1s
            .Returns(new SegmentMatch(220, 520, 200, 500));  // credits: B-side (E02) 1460.0s..

        var result = await NewService(db).DetectAsync(series.Id);

        Assert.Null(result.FailureReason);
        // Two working episodes (E01 + the E02 representative) — the duplicate is never decoded.
        _extractor.Verify(e => e.ExtractHeadAsync(It.IsAny<string>(), It.IsAny<double>(), It.IsAny<CancellationToken>()), Times.Exactly(2));

        var storedDup = await db.MediaItems.FirstAsync(m => m.Id == dup.Id);
        var storedRep = await db.MediaItems.FirstAsync(m => m.Id == ep2.Id);
        Assert.Equal(DetectionSource.Detected, storedDup.IntroSource);
        Assert.Equal(storedRep.IntroStart, storedDup.IntroStart);
        Assert.Equal(storedRep.IntroEnd, storedDup.IntroEnd);
        Assert.Equal(DetectionSource.Detected, storedDup.CreditsSource);
        Assert.Equal(storedRep.CreditsStart, storedDup.CreditsStart);
        Assert.NotNull(storedDup.LastIntroDetectionUtc); // stamped — no retry loop on every scan
    }

    [Fact]
    public async Task DetectAsync_DuplicateWithChapterMarkers_KeepsThem()
    {
        // The duplicate file carries its own embedded chapter markers — inheritance must
        // not overwrite them (chapter markers are authoritative over detection).
        await using var db = NewDb();
        var series = AddSeriesWithEpisodes(db, episodeCount: 2, episodeDuration: 1800);
        var dup = AddDuplicateEpisode(db, db.MediaItems.First(m => m.Id == series.Id), 1, 2, 1800,
            Guid.Parse("ffffffff-ffff-ffff-ffff-fffffffffffe"));
        dup.IntroSource = DetectionSource.Chapter;
        dup.IntroStart = 5;
        dup.IntroEnd = 30;
        db.SaveChanges();

        _extractor.Setup(e => e.ExtractHeadAsync(It.IsAny<string>(), It.IsAny<double>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(BuildFingerprint(length: 6000));
        _extractor.Setup(e => e.ExtractTailAsync(It.IsAny<string>(), It.IsAny<double>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(BuildFingerprint(length: 3600));
        _matcher.SetupSequence(m => m.FindLongestMatch(It.IsAny<uint[]>(), It.IsAny<uint[]>(), It.IsAny<int>(), It.IsAny<int>()))
            .Returns(new SegmentMatch(120, 420, 100, 400))
            .Returns(new SegmentMatch(220, 520, 200, 500));

        await NewService(db).DetectAsync(series.Id);

        var storedDup = await db.MediaItems.FirstAsync(m => m.Id == dup.Id);
        Assert.Equal(DetectionSource.Chapter, storedDup.IntroSource);
        Assert.Equal(5, storedDup.IntroStart);
        Assert.Equal(30, storedDup.IntroEnd);
        // Credits had no chapter claim — those ARE inherited.
        Assert.Equal(DetectionSource.Detected, storedDup.CreditsSource);
    }

    [Fact]
    public async Task DetectAsync_DifferentDurationDuplicate_IsFingerprintedIndependently()
    {
        // An extended cut shares (Season, Episode) but not the runtime — its markers sit
        // elsewhere, so it must be fingerprinted itself, not inherit.
        await using var db = NewDb();
        var series = AddSeriesWithEpisodes(db, episodeCount: 2, episodeDuration: 1800);
        AddDuplicateEpisode(db, db.MediaItems.First(m => m.Id == series.Id), 1, 2, 2400,
            Guid.Parse("ffffffff-ffff-ffff-ffff-fffffffffffe"));

        _extractor.Setup(e => e.ExtractHeadAsync(It.IsAny<string>(), It.IsAny<double>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(BuildFingerprint(length: 6000));
        _extractor.Setup(e => e.ExtractTailAsync(It.IsAny<string>(), It.IsAny<double>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(BuildFingerprint(length: 3600));
        _matcher.Setup(m => m.FindLongestMatch(It.IsAny<uint[]>(), It.IsAny<uint[]>(), It.IsAny<int>(), It.IsAny<int>()))
            .Returns((SegmentMatch?)null);

        await NewService(db).DetectAsync(series.Id);

        // All three rows decoded: E01, E02, and the different-cut copy.
        _extractor.Verify(e => e.ExtractHeadAsync(It.IsAny<string>(), It.IsAny<double>(), It.IsAny<CancellationToken>()), Times.Exactly(3));
    }

    private IntroCreditsDetectionService NewService(AppDbContext db)
        => new(db, _extractor.Object, _matcher.Object, _settings.Object, NullLogger<IntroCreditsDetectionService>.Instance);

    private static AppDbContext NewDb()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new AppDbContext(options);
    }

    private static MediaItem AddSeriesWithEpisodes(AppDbContext db, int episodeCount, double episodeDuration)
    {
        var libraryId = Guid.NewGuid();
        var series = new MediaItem
        {
            Id = Guid.NewGuid(),
            LibraryId = libraryId,
            Title = "Test Series",
            Path = "/series",
            Type = MediaType.Series
        };
        db.MediaItems.Add(series);

        for (int i = 1; i <= episodeCount; i++)
        {
            db.MediaItems.Add(new MediaItem
            {
                Id = Guid.NewGuid(),
                LibraryId = libraryId,
                Title = $"S01E{i:00}",
                Path = $"/series/s01e{i:00}.mkv",
                Type = MediaType.Episode,
                SeriesId = series.Id,
                SeasonNumber = 1,
                EpisodeNumber = i,
                Duration = episodeDuration
            });
        }
        db.SaveChanges();
        return series;
    }

    private static uint[] BuildFingerprint(int length)
    {
        // Deterministic distinct values — the matcher is mocked, so the actual content
        // is irrelevant; only Length matters for round-tripping through HashesToBytes.
        var fp = new uint[length];
        for (int i = 0; i < length; i++) fp[i] = (uint)(i + 1);
        return fp;
    }
}
