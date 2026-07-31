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
    /// <summary>
    /// Probed dimensions → coarse resolution tag; null when the file was never probed.
    /// WIDTH matters as much as height: widescreen "scope" encodes crop the pixel
    /// height (a 2.35:1 movie at 1080p is ~1920×816, at 4K ~3840×1608), so a
    /// height-only rule under-labels every cinemascope file by a full tier. Thresholds
    /// deliberately mirror the client's MediaQualityInfo panel so the versions list and
    /// the quality header can never disagree about the same file.
    /// </summary>
    public static string? ResolutionLabel(int? width, int? height)
    {
        var h = height ?? 0;
        var w = width ?? 0;
        if (h <= 0 && w <= 0) return null;

        if (h >= 4300 || w >= 7600) return "8K";
        if (h >= 2100 || w >= 3800) return "4K";
        if (h >= 1400 || w >= 2500) return "1440p";
        if (h >= 1000 || w >= 1900) return "1080p";
        if (h >= 700 || w >= 1260) return "720p";
        if (h >= 480 || w >= 840) return "480p";
        if (h >= 360 || w >= 640) return "360p";
        if (h >= 240 || w >= 420) return "240p";
        return h > 0 ? $"{h}p" : $"{w}w";
    }

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
        var resolution = ResolutionLabel(item.Width, item.Height);
        if (resolution != null) parts.Add(resolution);
        if (!string.IsNullOrEmpty(item.HdrFormat)) parts.Add(item.HdrFormat);
        var edition = EditionLabel(item.Path);
        if (edition != null) parts.Add(edition);

        if (parts.Count > 0) return string.Join(" ", parts);
        return string.IsNullOrEmpty(item.Container) ? "Original" : item.Container.ToUpperInvariant();
    }
}
