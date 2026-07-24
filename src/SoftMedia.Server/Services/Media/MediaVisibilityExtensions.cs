using SoftMedia.Server.Models;

namespace SoftMedia.Server.Services.Media;

/// <summary>
/// SR-WI-011 — missing items (file vanished from disk; soft-deleted) are hidden from
/// catalog surfaces but still resolve by id so detail pages and playlists can render
/// an "unavailable" row instead of losing the entry.
/// </summary>
public static class MediaVisibilityExtensions
{
    public static IQueryable<MediaItem> ExcludeMissing(this IQueryable<MediaItem> query)
        => query.Where(m => !m.IsMissing);
}
