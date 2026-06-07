using System.IO;
using System.Text.RegularExpressions;

namespace SoftMedia.Server.Helpers;

/// <summary>
/// Helpers for normalizing music album names and deriving disc numbers so that
/// multi-disc / multi-CD releases collapse into a single album instead of one
/// album per disc.
/// <para>
/// Tags in the wild encode the disc three inconsistent ways, sometimes at once:
/// an embedded Disc tag, a <c>"(CD2)"</c> / <c>"- CD 2: subtitle"</c> suffix on the
/// album title, or a <c>"CD2"</c> / <c>"Disc 2"</c> subfolder. These helpers
/// reconcile all three. Only the literal markers <c>disc</c>/<c>disk</c>/<c>cd</c>
/// followed by a number are treated as disc designators — <c>Volume</c>/<c>Part</c>
/// and roman numerals (e.g. "Use Your Illusion II", "Volume 8 - The Threat Is Real")
/// are intentionally left intact because they denote separate releases, not discs of
/// one album.
/// </para>
/// </summary>
public static class MusicNaming
{
    // Trailing "- CD 2", "- CD 2: Seeds of War", ", Disc 1" — a dash/comma separator
    // followed by a disc marker, with an optional subtitle running to the end.
    private static readonly Regex TrailingDiscDashed = new(
        @"\s*[-–—,]\s*(?:disc|disk|cd)\s*\.?\s*(\d{1,2})\b.*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Trailing "(CD1)", "[Disc 2]", " CD1", " Disc 3" — bracketed or bare, no subtitle.
    // The look-behind keeps the marker from gluing onto a preceding word
    // ("MixCD1" must NOT become "Mix"); a separator/bracket/space must precede it.
    private static readonly Regex TrailingDiscBracketed = new(
        @"[\s(\[{]*(?<![A-Za-z0-9])(?:disc|disk|cd)\s*\.?\s*(\d{1,2})\s*[)\]}]*\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // A folder whose whole name denotes a disc, e.g. "CD1", "CD 1", "Disc 2", "Disk 03".
    private static readonly Regex DiscFolder = new(
        @"^\s*(?:disc|disk|cd)\s*\.?\s*(\d{1,2})\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Strip a trailing disc designator from an album title, returning the canonical
    /// album name and the disc number if one was found. Returns the input unchanged
    /// (with a null disc) when no disc marker is present, or when stripping it would
    /// consume the entire title (e.g. an album literally named "CD1").
    /// </summary>
    public static (string Name, int? Disc) NormalizeAlbumName(string? albumName)
    {
        var name = (albumName ?? string.Empty).Trim();
        if (name.Length == 0)
            return (name, null);

        foreach (var rx in new[] { TrailingDiscDashed, TrailingDiscBracketed })
        {
            var m = rx.Match(name);
            if (!m.Success || m.Index == 0)
                continue; // m.Index == 0 would strip the whole title — keep it as-is.

            var stripped = name.Substring(0, m.Index).Trim();
            if (stripped.Length == 0)
                continue;

            return (stripped, int.TryParse(m.Groups[1].Value, out var d) ? d : (int?)null);
        }

        return (name, null);
    }

    /// <summary>
    /// Disc number parsed from a track's immediate parent folder ("CD2"/"Disc 2"),
    /// or null when the folder is not a disc folder.
    /// </summary>
    public static int? ParseDiscNumberFromPath(string trackPath)
    {
        var dir = Path.GetFileName(Path.GetDirectoryName(trackPath) ?? string.Empty);
        var m = DiscFolder.Match(dir);
        return m.Success && int.TryParse(m.Groups[1].Value, out var d) ? d : (int?)null;
    }

    /// <summary>
    /// The directory that represents the album: the track's parent folder, or its
    /// grandparent when the track lives in a "CD2"/"Disc 2" disc subfolder — so the
    /// album points at the real release folder (better for local cover-art lookup).
    /// </summary>
    public static string GetAlbumDirectory(string trackPath)
    {
        var dir = Path.GetDirectoryName(trackPath);
        if (string.IsNullOrEmpty(dir))
            return trackPath;

        var folderName = Path.GetFileName(dir);
        if (DiscFolder.IsMatch(folderName))
        {
            var parent = Path.GetDirectoryName(dir);
            if (!string.IsNullOrEmpty(parent))
                return parent;
        }

        return dir;
    }
}
