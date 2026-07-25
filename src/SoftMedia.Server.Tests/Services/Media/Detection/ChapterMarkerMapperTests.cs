using SoftMedia.Server.Services.Media.Detection;
using Xunit;

namespace SoftMedia.Server.Tests.Services.Media.Detection;

/// <summary>
/// CM-WI-001: chapter→marker mapping semantics. The mapper is the single source of truth
/// for which chapter titles mean intro/credits, the positional sanity guards, and the
/// next-chapter-start span derivation — these tests are the contract for all of that.
/// </summary>
public class ChapterMarkerMapperTests
{
    private static List<(double StartTime, string Title)> Chapters(params (double, string)[] c) => c.ToList();

    // ── The real-world case that motivated the feature (Futurama 09x10) ──────────

    [Fact]
    public void Maps_IntroSceneCredits_Layout()
    {
        var result = ChapterMarkerMapper.Map(
            Chapters((0, "Intro"), (32.324, "Scene 1"), (1437.853, "Credits")), durationSeconds: 1486.736);

        Assert.NotNull(result.Intro);
        Assert.Equal(0, result.Intro!.Start);
        Assert.Equal(32.324, result.Intro.End);       // next chapter start
        Assert.NotNull(result.Credits);
        Assert.Equal(1437.853, result.Credits!.Start);
        Assert.Equal(1486.736, result.Credits.End);   // last chapter → file duration
    }

    // ── Title matching ───────────────────────────────────────────────────────────

    [Theory]
    [InlineData("Intro")]
    [InlineData("OPENING")]
    [InlineData("  Opening Credits  ")]
    [InlineData("Main Titles")]
    [InlineData("OP")]
    [InlineData("Sigla")]
    public void IntroTitles_Match(string title)
    {
        var result = ChapterMarkerMapper.Map(Chapters((0, title), (40, "Part 1")), 1400);
        Assert.NotNull(result.Intro);
    }

    [Theory]
    [InlineData("Chapter 1")]
    [InlineData("Scene 1")]
    [InlineData("Recap")]
    [InlineData("Prologue")]
    [InlineData("Introduction to Robotics")] // exact-match by design: substrings are how false positives happen
    public void GenericOrContentTitles_DoNotMatchIntro(string title)
    {
        var result = ChapterMarkerMapper.Map(Chapters((0, title), (40, "Part 1")), 1400);
        Assert.Null(result.Intro);
    }

    [Theory]
    [InlineData("Credits")]
    [InlineData("End Credits")]
    [InlineData("end credits & outtakes")] // contains("credit") variant
    [InlineData("Outro")]
    [InlineData("Ending")]
    [InlineData("Titoli di coda")]
    public void CreditsTitles_Match(string title)
    {
        var result = ChapterMarkerMapper.Map(Chapters((0, "Part 1"), (1300, title)), 1400);
        Assert.NotNull(result.Credits);
        Assert.Equal(1300, result.Credits!.Start);
    }

    [Theory]
    [InlineData("Post-Credits Scene")]
    [InlineData("Mid-Credits Scene")]
    [InlineData("After Credits")]
    public void CreditsSceneChapters_AreContent_NotCredits(string title)
    {
        var result = ChapterMarkerMapper.Map(Chapters((0, "Part 1"), (1300, title)), 1400);
        Assert.Null(result.Credits);
    }

    // ── Post-credits handling: first credits match wins, scene bounds the span ──

    [Fact]
    public void PostCreditsScene_BoundsTheCreditsSpan()
    {
        var result = ChapterMarkerMapper.Map(
            Chapters((0, "Feature"), (5000, "End Credits"), (5400, "Post-Credits Scene")), 5500);

        Assert.NotNull(result.Credits);
        Assert.Equal(5000, result.Credits!.Start);
        Assert.Equal(5400, result.Credits.End); // skip-credits lands ON the scene, not past it
    }

    // ── Positional sanity guards ─────────────────────────────────────────────────

    [Fact]
    public void IntroChapter_DeepIntoTheFile_IsRejected()
    {
        // "Opening" of act 2 at 40% of runtime is content, not a skippable intro.
        var result = ChapterMarkerMapper.Map(
            Chapters((0, "Part 1"), (560, "Opening"), (700, "Part 2")), 1400);
        Assert.Null(result.Intro);
    }

    [Fact]
    public void IntroChapter_Past10Minutes_IsRejected_EvenInLongFiles()
    {
        var result = ChapterMarkerMapper.Map(
            Chapters((0, "Part 1"), (700, "Intro"), (760, "Part 2")), 7200);
        Assert.Null(result.Intro);
    }

    [Fact]
    public void CreditsChapter_InFirstHalf_IsRejected()
    {
        // "The Ending" as a mid-film content chapter must not mark credits.
        var result = ChapterMarkerMapper.Map(
            Chapters((0, "Part 1"), (500, "Ending"), (900, "Part 3")), 1400);
        Assert.Null(result.Credits);
    }

    // Real file found in live QA: "My Three Suns" — the authoring skipped a chapter, so
    // "Opening Credits" spans 471 s. Broken authoring must yield NO marker (detection
    // fills the gap), never an 8-minute skip target.
    [Fact]
    public void ImplausiblyLongIntroSpan_IsRejected_MyThreeSunsCase()
    {
        var result = ChapterMarkerMapper.Map(
            Chapters((0, "Scene 1"), (54.012, "Opening Credits"), (525.025, "Scene 3"), (1314.146, "End Credits")),
            1352.384);

        Assert.Null(result.Intro);              // 471s span rejected
        Assert.NotNull(result.Credits);         // credits chapter is fine (38s)
        Assert.Equal(1314.146, result.Credits!.Start);
    }

    [Fact]
    public void ImplausiblyLongCreditsSpan_IsRejected()
    {
        // A credits-titled chapter covering the whole second half is broken authoring.
        var result = ChapterMarkerMapper.Map(
            Chapters((0, "Part 1"), (3600, "Credits")), 7200);
        Assert.Null(result.Credits); // 3600s span exceeds the 15-minute ceiling
    }

    [Fact]
    public void TinySpans_AreAuthoringNoise()
    {
        var result = ChapterMarkerMapper.Map(
            Chapters((0, "Intro"), (2, "Part 1"), (1398, "Credits")), 1400);
        Assert.Null(result.Intro);   // 2s intro span
        Assert.Null(result.Credits); // 2s credits span
    }

    // ── Degenerate inputs ────────────────────────────────────────────────────────

    [Fact]
    public void SingleChapter_CarriesNoSegmentInformation()
    {
        var result = ChapterMarkerMapper.Map(Chapters((0, "Intro")), 1400);
        Assert.Null(result.Intro);
        Assert.Null(result.Credits);
    }

    [Fact]
    public void EmptyList_And_ZeroDuration_MapToEmpty()
    {
        Assert.Equal(ChapterMarkerResult.Empty, ChapterMarkerMapper.Map(Chapters(), 1400));
        Assert.Equal(ChapterMarkerResult.Empty,
            ChapterMarkerMapper.Map(Chapters((0, "Intro"), (30, "Part 1")), 0));
    }

    [Fact]
    public void IntroAsFinalChapter_IsRejected_NoSkipTargetExists()
    {
        var result = ChapterMarkerMapper.Map(Chapters((0, "Cold Open"), (20, "Intro")), 1400);
        Assert.Null(result.Intro);
    }

    [Fact]
    public void UnsortedInput_IsHandled()
    {
        var result = ChapterMarkerMapper.Map(
            Chapters((1437.853, "Credits"), (0, "Intro"), (32.324, "Scene 1")), 1486.736);
        Assert.NotNull(result.Intro);
        Assert.Equal(32.324, result.Intro!.End);
        Assert.NotNull(result.Credits);
    }
}
