using SoftMedia.Server.Models;

namespace SoftMedia.Server.Services.Media;

/// <summary>
/// DV-WI-015 — THE primary-version rule (plan §2.2), in its two required forms:
/// an in-memory ordering and a translatable SQL filter. The primary is COMPUTED, never
/// stored: explicit PreferredVersion override → max height → HDR present → max bitrate
/// → newest → Path (a unique, SQL-comparable total-order tiebreaker; Guid comparison
/// does not translate).
///
/// KEEP THE TWO FORMS ALIGNED — <see cref="OrderPrimaryFirst"/> and
/// <see cref="OnePerVersionGroup"/> encode the same lexicographic rule; a divergence
/// makes the detail page's primary differ from the card the grid shows.
/// </summary>
public static class VersionPrimaryRule
{
    /// <summary>Members of one group, primary first (in-memory form).</summary>
    public static IOrderedEnumerable<MediaItem> OrderPrimaryFirst(IEnumerable<MediaItem> members) =>
        members
            .OrderByDescending(s => s.PreferredVersion)
            .ThenByDescending(s => s.Height ?? 0)
            .ThenByDescending(s => !string.IsNullOrEmpty(s.HdrFormat))
            .ThenByDescending(s => s.Bitrate ?? 0)
            .ThenByDescending(s => s.DateAdded)
            .ThenBy(s => s.Path, StringComparer.Ordinal);

    /// <summary>
    /// Queryable form: keeps ungrouped rows and, per version group, only the primary —
    /// "no other live member of my group outranks me". Composes with any prior
    /// filtering; pass the unfiltered <c>MediaItems</c> set as <paramref name="all"/>
    /// so the primary choice is consistent regardless of the caller's own filters
    /// (a rating-blocked primary hides the whole group rather than silently promoting
    /// a copy the rule ranks lower — groups share one title, so their ratings agree).
    /// </summary>
    public static IQueryable<MediaItem> OnePerVersionGroup(this IQueryable<MediaItem> query, IQueryable<MediaItem> all)
        => query.Where(m => m.VersionGroupId == null || !all.Any(o =>
            o.VersionGroupId == m.VersionGroupId && o.Id != m.Id && !o.IsMissing &&
            (
                (o.PreferredVersion && !m.PreferredVersion) ||
                (o.PreferredVersion == m.PreferredVersion && (
                    (o.Height ?? 0) > (m.Height ?? 0) ||
                    ((o.Height ?? 0) == (m.Height ?? 0) && (
                        ((o.HdrFormat != null && o.HdrFormat != "") && (m.HdrFormat == null || m.HdrFormat == "")) ||
                        (((o.HdrFormat != null && o.HdrFormat != "") == (m.HdrFormat != null && m.HdrFormat != "")) && (
                            (o.Bitrate ?? 0) > (m.Bitrate ?? 0) ||
                            ((o.Bitrate ?? 0) == (m.Bitrate ?? 0) && (
                                o.DateAdded > m.DateAdded ||
                                (o.DateAdded == m.DateAdded && string.Compare(o.Path, m.Path) < 0)))))))))
            )));

    /// <summary>
    /// Group-level interaction view for a collapsed row: watched when ANY copy is
    /// watched; resume state from the most recently played copy (positions are
    /// wall-clock comparable across copies of one cut). Null when no copy has state.
    /// </summary>
    public static UserMediaInteraction? MergeGroupInteraction(
        IReadOnlyCollection<UserMediaInteraction> memberInteractions)
    {
        if (memberInteractions.Count == 0) return null;

        var latest = memberInteractions
            .OrderByDescending(i => i.LastPlayed ?? DateTime.MinValue)
            .First();
        var anyWatched = memberInteractions.Any(i => i.IsWatched);
        if (latest.IsWatched == anyWatched) return latest;

        // Copy, don't mutate — these are tracked entities in some callers.
        return new UserMediaInteraction
        {
            UserId = latest.UserId,
            MediaItemId = latest.MediaItemId,
            IsWatched = anyWatched,
            PlaybackPosition = latest.PlaybackPosition,
            LastPlayed = latest.LastPlayed,
            Rating = latest.Rating,
            IsFavorite = latest.IsFavorite,
            IsWatchlisted = latest.IsWatchlisted,
            WatchlistedAt = latest.WatchlistedAt,
            BookLocation = latest.BookLocation,
        };
    }
}
