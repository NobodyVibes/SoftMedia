namespace SoftMedia.Server.Constants;

/// <summary>
/// NR-WI-014 — companion-clip conventions (Radarr/Kodi): a video beside the main file
/// whose stem carries one of these suffixes, or any video inside one of these
/// subfolders, is an EXTRA of the title — never its own library item. Shared by the
/// scanners (which skip them), LocalArtworkService (folder-detection heuristic), and
/// ExtrasService (which surfaces them on the detail page).
/// </summary>
public static class MediaCompanions
{
    public static readonly string[] Suffixes = { "-trailer", "-sample", "-extra", "-featurette", "-behindthescenes", "-deleted" };

    public static readonly string[] Folders = { "extras", "trailers", "samples", "featurettes", "behind the scenes", "deleted scenes" };

    public static bool HasCompanionSuffix(string filePath)
    {
        var stem = Path.GetFileNameWithoutExtension(filePath);
        return Suffixes.Any(s => stem.EndsWith(s, StringComparison.OrdinalIgnoreCase));
    }

    public static bool IsInCompanionFolder(string filePath)
    {
        var parent = Path.GetFileName(Path.GetDirectoryName(filePath) ?? "");
        return Folders.Contains(parent, StringComparer.OrdinalIgnoreCase);
    }

    public static bool IsCompanion(string filePath) =>
        HasCompanionSuffix(filePath) || IsInCompanionFolder(filePath);
}
