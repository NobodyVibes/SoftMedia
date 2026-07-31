using System.Security.Cryptography;
using SoftMedia.Server.Models;

namespace SoftMedia.Server.Helpers;

/// <summary>
/// DV-WI-010 — identity rules for version groups (plan §2.1): which files are copies of
/// one logical title. Pure functions only; assignment/persistence lives in
/// <c>VersionGroupAssigner</c>.
/// </summary>
public static class VersionGroupHelper
{
    /// <summary>
    /// Deterministic group id for an episode: an MD5-derived GUID over
    /// (SeriesId, Season, Episode). Every scan worker — and the boot backfill — computes
    /// the same id for the same episode, so duplicate rows converge on one group with no
    /// coordination. Never call with episodeNumber &lt;= 0: unparseable files are NOT
    /// duplicates of each other.
    /// </summary>
    public static Guid ComputeEpisodeGroupId(Guid seriesId, int seasonNumber, int episodeNumber)
    {
        Span<byte> buffer = stackalloc byte[24];
        seriesId.TryWriteBytes(buffer);
        BitConverter.TryWriteBytes(buffer[16..], seasonNumber);
        BitConverter.TryWriteBytes(buffer[20..], episodeNumber);
        Span<byte> hash = stackalloc byte[16];
        MD5.HashData(buffer, hash);
        return new Guid(hash);
    }

    /// <summary>
    /// Movie identity key: casefolded alphanumerics only, so "Movie!" / "movie" collide
    /// while extra WORDS ("Movie Copy") remain distinguishing — word-level fuzzing
    /// merges too eagerly (remuxes named "The Copy", "Extended" belong to editions).
    /// A missed pair is one admin merge away and visible in the duplicates report.
    /// </summary>
    public static string NormalizeTitleKey(string title)
        => new string(title.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();

    /// <summary>
    /// Same-movie test (plan §2.1). Provider identity is authoritative when BOTH sides
    /// carry one: equal → same movie regardless of title/year drift; different → never
    /// the same, however identical the titles (remakes sharing a year). Otherwise the
    /// (normalized title, year) heuristic decides; a null year only matches a null year
    /// here — the scanner stamps years from filenames, so a missing year usually means a
    /// missing year on both copies.
    /// </summary>
    public static bool AreSameMovie(MediaItem a, MediaItem b)
    {
        if (!string.IsNullOrEmpty(a.ImdbId) && !string.IsNullOrEmpty(b.ImdbId))
            return string.Equals(a.ImdbId, b.ImdbId, StringComparison.OrdinalIgnoreCase);
        if (a.Year != b.Year) return false;
        return NormalizeTitleKey(a.Title) == NormalizeTitleKey(b.Title);
    }
}
