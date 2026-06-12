using SoftMedia.Server.Models;

namespace SoftMedia.Server.Services.Dlna;

/// <summary>
/// DLNA exposure policy (security audit M7/L9). DLNA is unauthenticated — TVs can't log in —
/// so instead of a per-user ACL the admin explicitly designates which libraries are exposed
/// via the <c>DlnaExposedLibraries</c> setting (a CSV of library GUIDs). The default is EMPTY =
/// NONE: enabling DLNA exposes nothing until the admin opts specific libraries in, so a fresh
/// "EnableDlna" flip can never silently publish a restricted/adult library to the whole LAN.
/// </summary>
public static class DlnaAccess
{
    public const string ExposedLibrariesSetting = "DlnaExposedLibraries";

    /// <summary>
    /// Optional per-type content-rating ceiling for DLNA (security audit wave-2 M-6). DLNA is
    /// unauthenticated, so the per-user rating ceiling can't apply; this lets the admin cap the
    /// maximum rating any LAN device sees. Stored as the same per-type JSON the user ceiling uses,
    /// e.g. <c>{"Movie":"PG-13","TV":"TV-PG"}</c>. Empty (default) = no extra cap — so exposing a
    /// mixed-rating library over DLNA without setting this leaves parental controls off for that
    /// surface (documented; admins who expose a library should also set the ceiling).
    /// </summary>
    public const string MaxContentRatingsSetting = "DlnaMaxContentRatings";

    /// <summary>Parses the CSV of library GUIDs into a list (empty => no library is exposed).</summary>
    public static List<Guid> ParseExposedLibraryIds(string? csv)
    {
        var result = new List<Guid>();
        if (string.IsNullOrWhiteSpace(csv)) return result;
        foreach (var part in csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (Guid.TryParse(part, out var id) && !result.Contains(id)) result.Add(id);
        return result;
    }

    /// <summary>Item types DLNA serves as a playable file. Containers (Series/Album) and non-AV
    /// types (Book/Photo/Game/Comic) are never served as a file via the DLNA media endpoint.</summary>
    public static bool IsStreamableType(MediaType type)
        => type is MediaType.Movie or MediaType.Episode or MediaType.Audio;
}
