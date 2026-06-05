namespace SoftMedia.Server.Services.Media.Detection;

/// <summary>
/// A range of matching hash indices found between two Chromaprint fingerprints.
/// Indices are inclusive on both ends.
/// </summary>
/// <param name="AStart">First matching index in fingerprint A.</param>
/// <param name="AEnd">Last matching index in fingerprint A (inclusive).</param>
/// <param name="BStart">First matching index in fingerprint B.</param>
/// <param name="BEnd">Last matching index in fingerprint B (inclusive).</param>
public record SegmentMatch(int AStart, int AEnd, int BStart, int BEnd)
{
    public int Length => AEnd - AStart + 1;
}

/// <summary>
/// Finds shared audio segments between two Chromaprint fingerprints. Used to locate
/// repeating intros and outros across episodes of the same series.
/// </summary>
public interface ISegmentMatcher
{
    /// <summary>
    /// Find the longest run of matching hashes between <paramref name="a"/> and
    /// <paramref name="b"/>. Two hashes match when their Hamming distance (population
    /// count of XOR) is ≤ <paramref name="maxBitErrors"/>.
    ///
    /// Returns null if no run reaches <paramref name="minLength"/> hashes.
    /// </summary>
    SegmentMatch? FindLongestMatch(uint[] a, uint[] b, int minLength, int maxBitErrors);
}
