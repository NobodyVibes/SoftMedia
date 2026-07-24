using SoftMedia.Server.Models;

namespace SoftMedia.Server.Services.Metadata;

/// <summary>
/// SR-WI-036 — maps a <see cref="MediaType"/> to the <see cref="LibraryType"/> the metadata
/// queue expects for provider routing. Scanners pass the owning library's type directly; paths
/// that only hold a MediaItem (global refresh across all types, retry amnesty, per-item admin
/// refresh) derive it here. The mapping is total and deterministic: every MediaType belongs to
/// exactly one library type.
/// </summary>
public static class MediaTypeLibraryMap
{
    public static LibraryType ForMediaType(MediaType type) => type switch
    {
        MediaType.Movie => LibraryType.Movie,
        MediaType.Series or MediaType.Season or MediaType.Episode => LibraryType.TV,
        MediaType.Audio or MediaType.Artist or MediaType.Album => LibraryType.Music,
        MediaType.Book or MediaType.ComicSeries or MediaType.ComicIssue => LibraryType.Book,
        MediaType.Game => LibraryType.Game,
        MediaType.Photo => LibraryType.Photo,
        _ => LibraryType.Movie,
    };
}
