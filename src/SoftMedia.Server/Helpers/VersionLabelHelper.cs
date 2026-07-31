using System.Text.RegularExpressions;
using SoftMedia.Server.Models;

namespace SoftMedia.Server.Helpers;

/// <summary>
/// DV-WI-013 — the ONE authority for human-readable version labels (plan §2.2), derived
/// from probed technical data plus an edition token parsed from the filename. Consumed
/// by the DTO surface, the admin duplicates report, and DLNA duplicate disambiguation —
/// the client renders these labels verbatim instead of re-deriving its own (which is how
/// the FHD/1080p/HD three-way drift happened).
/// </summary>
public static partial class VersionLabelHelper
{
    /// <summary>Height → coarse resolution tag; null when the file was never probed.</summary>
    public static string? ResolutionLabel(int? height) => height switch
    {
        null or <= 0 => null,
        >= 4320 => "8K",
        >= 2160 => "4K",
        >= 1440 => "1440p",
        >= 1080 => "1080p",
        >= 720 => "720p",
        >= 480 => "480p",
        _ => $"{height}p",
    };

    [GeneratedRegex(@"\b(director'?s[ ._-]?cut|extended|theatrical|unrated|uncut|remastered|imax)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex EditionTokenRegex();

    /// <summary>Edition token from the file name ("Movie (2010) Director's Cut.mkv").</summary>
    public static string? EditionLabel(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        var stem = Path.GetFileNameWithoutExtension(path);
        var match = EditionTokenRegex().Match(stem);
        if (!match.Success) return null;

        var token = Regex.Replace(match.Value, @"[ ._-]+", " ").ToLowerInvariant();
        return token switch
        {
            "directors cut" or "director's cut" => "Director's Cut",
            "extended" => "Extended",
            "theatrical" => "Theatrical",
            "unrated" => "Unrated",
            "uncut" => "Uncut",
            "remastered" => "Remastered",
            "imax" => "IMAX",
            _ => null,
        };
    }

    /// <summary>
    /// Full display label, e.g. "4K HDR10 Director's Cut" or "1080p". Falls back to the
    /// container name for never-probed files so two duplicates are still tellable apart.
    /// </summary>
    public static string BuildLabel(MediaItem item)
    {
        var parts = new List<string>(3);
        var resolution = ResolutionLabel(item.Height);
        if (resolution != null) parts.Add(resolution);
        if (!string.IsNullOrEmpty(item.HdrFormat)) parts.Add(item.HdrFormat);
        var edition = EditionLabel(item.Path);
        if (edition != null) parts.Add(edition);

        if (parts.Count > 0) return string.Join(" ", parts);
        return string.IsNullOrEmpty(item.Container) ? "Original" : item.Container.ToUpperInvariant();
    }
}
