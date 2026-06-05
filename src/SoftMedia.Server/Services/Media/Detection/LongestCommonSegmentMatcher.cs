using System.Numerics;

namespace SoftMedia.Server.Services.Media.Detection;

/// <summary>
/// Pairwise fingerprint matcher: builds an inverted index of fingerprint A, walks
/// fingerprint B, and for each exact hash collision extends in both directions using
/// a Hamming-distance tolerance. The longest extended run that meets the minimum
/// length threshold is returned.
///
/// Why exact-match seeds + tolerant extension instead of a full Hamming-tolerant
/// search? Tolerant search is O(n*m) and slow; exact-match seeding is sparse enough
/// in real audio that we get full coverage of the truly-shared segment without ever
/// computing a full distance table. This is the same shape as Jellyfin's intro
/// skipper algorithm and the Chromaprint reference matcher.
/// </summary>
public class LongestCommonSegmentMatcher : ISegmentMatcher
{
    /// <summary>
    /// Hashes that occur more than this many times in fingerprint A are skipped as
    /// candidate seeds. They tend to be silence or low-information frames whose
    /// collisions explode the seed count without ever leading to a real match.
    /// </summary>
    private const int MaxSeedOccurrences = 50;

    /// <summary>
    /// Hamming-distance ceiling used to trim the matched segment back from each
    /// end after greedy extension. Greedy extension legitimately walks through
    /// silence, room tone, and coincidentally-similar B-roll up to the
    /// caller-supplied maxBitErrors, but those frames usually aren't part of the
    /// real intro/outro theme — they shift the boundary 5–30 s past the actual
    /// theme end. Trimming with a stricter threshold (≤ 2 bit errors) keeps only
    /// the high-confidence core of the match. The seed itself is an exact match
    /// (0 bits) so the trim can never cross it.
    /// </summary>
    private const int TrimMaxBitErrors = 2;

    public SegmentMatch? FindLongestMatch(uint[] a, uint[] b, int minLength, int maxBitErrors)
    {
        if (a.Length == 0 || b.Length == 0 || minLength <= 0)
            return null;

        var indexA = BuildInvertedIndex(a);

        SegmentMatch? best = null;

        // Track which (a-index, b-index) pairs have already been covered by a found
        // segment so we don't re-extend from a seed inside an existing match.
        var coveredA = new HashSet<long>();

        for (int j = 0; j < b.Length; j++)
        {
            if (!indexA.TryGetValue(b[j], out var positions)) continue;
            if (positions.Count > MaxSeedOccurrences) continue;

            foreach (var i in positions)
            {
                // Cheap pre-check: an existing match starting earlier already covered this seed.
                if (coveredA.Contains(PackPair(i, j))) continue;

                var (sa, sb, ea, eb) = Extend(a, b, i, j, maxBitErrors);
                var length = ea - sa + 1;

                if (length >= minLength && (best == null || length > best.Length))
                {
                    best = new SegmentMatch(sa, ea, sb, eb);
                }

                // Mark the diagonal of this extension as covered. Subsequent seeds
                // landing inside the same diagonal will short-circuit above.
                for (int k = 0; k <= length; k++)
                {
                    coveredA.Add(PackPair(sa + k, sb + k));
                }
            }
        }

        return best;
    }

    private static Dictionary<uint, List<int>> BuildInvertedIndex(uint[] a)
    {
        var index = new Dictionary<uint, List<int>>(a.Length);
        for (int i = 0; i < a.Length; i++)
        {
            if (!index.TryGetValue(a[i], out var list))
            {
                list = new List<int>(1);
                index[a[i]] = list;
            }
            list.Add(i);
        }
        return index;
    }

    private static (int sa, int sb, int ea, int eb) Extend(uint[] a, uint[] b, int i, int j, int maxBitErrors)
    {
        // Phase 1 — greedy extension with the caller's tolerance. Walks through
        // anything that's similar enough to count as "the same audio" with some
        // compression jitter.
        int sa = i, sb = j;
        while (sa > 0 && sb > 0 && HammingDistance(a[sa - 1], b[sb - 1]) <= maxBitErrors)
        {
            sa--;
            sb--;
        }

        int ea = i, eb = j;
        while (ea + 1 < a.Length && eb + 1 < b.Length && HammingDistance(a[ea + 1], b[eb + 1]) <= maxBitErrors)
        {
            ea++;
            eb++;
        }

        // Phase 2 — trim low-confidence edges back toward the seed. The greedy
        // pass over-extends through silence, room tone, and coincidentally-similar
        // dialogue (frames within maxBitErrors but above TrimMaxBitErrors); those
        // are not part of the actual repeating intro/outro theme. We trim until
        // the boundary frame on each side is a high-confidence match.
        while (ea > i && HammingDistance(a[ea], b[eb]) > TrimMaxBitErrors)
        {
            ea--;
            eb--;
        }

        while (sa < i && HammingDistance(a[sa], b[sb]) > TrimMaxBitErrors)
        {
            sa++;
            sb++;
        }

        return (sa, sb, ea, eb);
    }

    private static int HammingDistance(uint x, uint y) => BitOperations.PopCount(x ^ y);

    /// <summary>
    /// Packs two int indices into a single long for hashset membership. Both indices
    /// fit in 31 bits, well within int range, so this is collision-free.
    /// </summary>
    private static long PackPair(int a, int b) => ((long)a << 32) | (uint)b;
}
