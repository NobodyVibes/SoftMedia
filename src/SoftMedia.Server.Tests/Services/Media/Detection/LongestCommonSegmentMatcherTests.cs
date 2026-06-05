using SoftMedia.Server.Services.Media.Detection;
using Xunit;

namespace SoftMedia.Server.Tests.Services.Media.Detection;

public class LongestCommonSegmentMatcherTests
{
    private readonly LongestCommonSegmentMatcher _matcher = new();

    [Fact]
    public void FindLongestMatch_ReturnsNull_WhenNoSharedHashes()
    {
        var a = new uint[] { 0x1000_0000, 0x2000_0000, 0x3000_0000 };
        var b = new uint[] { 0xA000_0000, 0xB000_0000, 0xC000_0000 };

        var result = _matcher.FindLongestMatch(a, b, minLength: 1, maxBitErrors: 6);

        Assert.Null(result);
    }

    [Fact]
    public void FindLongestMatch_ReturnsNull_WhenSharedSegmentBelowMinLength()
    {
        var shared = new uint[] { 0x11, 0x22, 0x33 };
        var a = Concat(new uint[] { 0xAA, 0xBB }, shared, new uint[] { 0xCC });
        var b = Concat(new uint[] { 0xDD }, shared, new uint[] { 0xEE });

        var result = _matcher.FindLongestMatch(a, b, minLength: 5, maxBitErrors: 0);

        Assert.Null(result);
    }

    [Fact]
    public void FindLongestMatch_FindsExactSharedSegment()
    {
        // Shared segment of length 5, embedded in unique surroundings on each side.
        var shared = new uint[] { 0x100, 0x200, 0x300, 0x400, 0x500 };
        var a = Concat(new uint[] { 0xAA, 0xBB, 0xCC }, shared, new uint[] { 0xDD });
        var b = Concat(new uint[] { 0xE1, 0xE2 }, shared, new uint[] { 0xE3, 0xE4, 0xE5 });

        var result = _matcher.FindLongestMatch(a, b, minLength: 5, maxBitErrors: 0);

        Assert.NotNull(result);
        Assert.Equal(5, result!.Length);
        Assert.Equal(3, result.AStart);
        Assert.Equal(7, result.AEnd);
        Assert.Equal(2, result.BStart);
        Assert.Equal(6, result.BEnd);
    }

    [Fact]
    public void FindLongestMatch_PrefersLongerSegment_WhenMultipleExist()
    {
        var shortShared = new uint[] { 0x900, 0xA00 };
        var longShared = new uint[] { 0x100, 0x200, 0x300, 0x400, 0x500 };
        var a = Concat(shortShared, new uint[] { 0xFF }, longShared, new uint[] { 0x77 });
        var b = Concat(new uint[] { 0xEE }, shortShared, new uint[] { 0xFE }, longShared);

        var result = _matcher.FindLongestMatch(a, b, minLength: 2, maxBitErrors: 0);

        Assert.NotNull(result);
        Assert.Equal(longShared.Length, result!.Length);
    }

    // Sentinels chosen so the Hamming distance between A's sentinel and B's sentinel
    // is 32 (full inversion) — guarantees bit-tolerant extension stops at the boundary.
    private const uint SentinelA = 0xFFFF_FFFF;
    private const uint SentinelB = 0x0000_0000;

    [Fact]
    public void FindLongestMatch_ToleratesBitErrors_WithinThreshold()
    {
        // a and b share a 5-element segment, but each element in b has 2 random bits
        // flipped relative to a. With maxBitErrors=6, the matcher must still find it.
        var aShared = new uint[] { 0xFFFF_FFFF, 0xAAAA_AAAA, 0x5555_5555, 0xFF00_FF00, 0x00FF_00FF };
        var bShared = new uint[]
        {
            0xFFFF_FFFF ^ 0b0000_0011u,  // 2 bits flipped
            0xAAAA_AAAA ^ 0b0000_0011u,
            0x5555_5555 ^ 0b0000_0011u,
            0xFF00_FF00 ^ 0b0000_0011u,
            0x00FF_00FF ^ 0b0000_0011u,
        };
        // The seed needs at least one *exact* match, so leave one element unflipped.
        bShared[2] = aShared[2];

        var a = Concat(new uint[] { SentinelA }, aShared, new uint[] { SentinelA });
        var b = Concat(new uint[] { SentinelB }, bShared, new uint[] { SentinelB });

        var result = _matcher.FindLongestMatch(a, b, minLength: 5, maxBitErrors: 6);

        Assert.NotNull(result);
        Assert.Equal(5, result!.Length);
    }

    [Fact]
    public void FindLongestMatch_RejectsBitErrors_AboveThreshold()
    {
        // Same setup as the tolerance test but with maxBitErrors=0 — only the single
        // exact-matching seed survives, which is below the minLength threshold.
        var aShared = new uint[] { 0xFFFF_FFFF, 0xAAAA_AAAA, 0x5555_5555, 0xFF00_FF00, 0x00FF_00FF };
        var bShared = new uint[]
        {
            0xFFFF_FFFF ^ 0b0000_0011u,
            0xAAAA_AAAA ^ 0b0000_0011u,
            0x5555_5555,                 // single exact match
            0xFF00_FF00 ^ 0b0000_0011u,
            0x00FF_00FF ^ 0b0000_0011u,
        };

        var a = Concat(new uint[] { SentinelA }, aShared, new uint[] { SentinelA });
        var b = Concat(new uint[] { SentinelB }, bShared, new uint[] { SentinelB });

        var result = _matcher.FindLongestMatch(a, b, minLength: 5, maxBitErrors: 0);

        Assert.Null(result);
    }

    [Fact]
    public void FindLongestMatch_ReturnsNull_OnEmptyInput()
    {
        var nonEmpty = new uint[] { 0x100, 0x200 };
        Assert.Null(_matcher.FindLongestMatch(Array.Empty<uint>(), nonEmpty, 1, 6));
        Assert.Null(_matcher.FindLongestMatch(nonEmpty, Array.Empty<uint>(), 1, 6));
    }

    [Fact]
    public void FindLongestMatch_TrimsLowConfidenceEdges_AfterGreedyExtension()
    {
        // Regression for the "skip pill jumps 30s into actual content" bug.
        //
        // Real-world setup: an intro theme that genuinely repeats across episodes
        // (5 exact-match hashes) followed by silence / room tone / opening dialogue
        // that happens to differ by ~5 bits — within the matcher's 6-bit tolerance,
        // so greedy extension walks through it, but NOT part of the actual intro.
        //
        // The trim pass must walk the low-confidence tail back to the high-confidence
        // core, otherwise we mark the intro as ending 5 frames (~half a second
        // per frame) past where the theme actually stops.
        var realIntro = new uint[] { 0x1111_1111, 0x2222_2222, 0x3333_3333, 0x4444_4444, 0x5555_5555 };

        // 5 bits flipped per element — within maxBitErrors=6 (greedy walks them)
        // but above TrimMaxBitErrors=2 (trim drops them).
        const uint NoiseMask = 0b0001_1111u;
        var noisyTail = new uint[] { 0xA000_0000, 0xB000_0000, 0xC000_0000, 0xD000_0000, 0xE000_0000 };
        var noisyTailB = noisyTail.Select(h => h ^ NoiseMask).ToArray();
        var noisyHead = new uint[] { 0x0A00_0000, 0x0B00_0000, 0x0C00_0000, 0x0D00_0000, 0x0E00_0000 };
        var noisyHeadB = noisyHead.Select(h => h ^ NoiseMask).ToArray();

        // Sentinels with full 32-bit Hamming distance pin the segment boundaries.
        var a = Concat(new uint[] { SentinelA }, noisyHead, realIntro, noisyTail, new uint[] { SentinelA });
        var b = Concat(new uint[] { SentinelB }, noisyHeadB, realIntro, noisyTailB, new uint[] { SentinelB });

        var result = _matcher.FindLongestMatch(a, b, minLength: 5, maxBitErrors: 6);

        Assert.NotNull(result);
        // Without the trim, length would be 15 (noisy head + intro + noisy tail).
        // With the trim, only the 5 exact-match hashes survive.
        Assert.Equal(realIntro.Length, result!.Length);

        // realIntro starts at index 6 in `a` (sentinel + 5 noisy head).
        Assert.Equal(6, result.AStart);
        Assert.Equal(10, result.AEnd);
    }

    [Fact]
    public void FindLongestMatch_HandlesIdenticalFingerprints()
    {
        // Two identical fingerprints — full-length match expected.
        var fp = new uint[] { 0x1, 0x2, 0x3, 0x4, 0x5, 0x6, 0x7, 0x8 };

        var result = _matcher.FindLongestMatch(fp, fp, minLength: 1, maxBitErrors: 0);

        Assert.NotNull(result);
        Assert.Equal(fp.Length, result!.Length);
        Assert.Equal(0, result.AStart);
        Assert.Equal(fp.Length - 1, result.AEnd);
    }

    private static uint[] Concat(params uint[][] arrays)
    {
        var total = 0;
        foreach (var a in arrays) total += a.Length;
        var result = new uint[total];
        var offset = 0;
        foreach (var a in arrays)
        {
            Array.Copy(a, 0, result, offset, a.Length);
            offset += a.Length;
        }
        return result;
    }
}
