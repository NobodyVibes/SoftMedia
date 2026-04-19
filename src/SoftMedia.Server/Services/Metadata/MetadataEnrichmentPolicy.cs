using SoftMedia.Server.Models;

namespace SoftMedia.Server.Services.Metadata;

/// <summary>
/// Provides a unified policy for determining whether a <see cref="MediaItem"/>
/// requires metadata enrichment. Used by all scanners and the metadata queue
/// to ensure consistent re-enrichment behavior across media types.
/// <para>
/// Supports two modes controlled by the <c>MetadataEnrichmentMode</c> setting:
/// <list type="bullet">
///   <item><b>Relaxed (default):</b> Item is complete when it has a poster/cover.</item>
///   <item><b>Strict:</b> Requires type-specific fields (description for movies, author for books, etc.).</item>
/// </list>
/// </para>
/// </summary>
public static class MetadataEnrichmentPolicy
{
    /// <summary>
    /// Backward-compatible overload that uses Relaxed mode (poster-only check).
    /// Existing callers continue to work without modification.
    /// </summary>
    public static bool NeedsEnrichment(MediaItem item) => NeedsEnrichment(item, strictMode: false);

    /// <summary>
    /// Determines whether the given <paramref name="item"/> needs metadata enrichment.
    /// <para>
    /// <b>Relaxed mode (<paramref name="strictMode"/> = false):</b> Item is considered
    /// complete if it has a poster URL or cover art path. This preserves existing behavior.
    /// </para>
    /// <para>
    /// <b>Strict mode (<paramref name="strictMode"/> = true):</b> Checks type-specific
    /// required fields beyond just a poster. Movies/Series also need a description.
    /// Books also need cast entries. Albums need poster or cover art on disk.
    /// Enabling strict mode may cause existing items to be re-queued for enrichment.
    /// </para>
    /// </summary>
    public static bool NeedsEnrichment(MediaItem item, bool strictMode)
    {
        // Items that have exhausted all retry attempts should never be re-queued.
        // MetadataRetryService sets IsRetryExhausted after max retries are reached.
        if (item.IsRetryExhausted)
        {
            return false;
        }

        // Check promoted columns instead of parsing MetadataJson.
        bool hasPoster = !string.IsNullOrEmpty(item.PosterUrl)
                      || !string.IsNullOrEmpty(item.CoverArtPath);

        // Comics have no external PosterUrl by design (covers live as page-1 images
        // inside the archive). Using `!hasPoster` as the relaxed-mode signal would
        // cause them to retry forever. Instead, treat comics as enriched once we've
        // attempted fetch at least once — MetadataAggregator stamps a sentinel
        // MetadataHash on null-result paths to represent that attempt.
        if (item.Type == MediaType.ComicSeries || item.Type == MediaType.ComicIssue)
        {
            return string.IsNullOrEmpty(item.MetadataHash);
        }

        // No metadata hash means metadata has never been fetched (except for sparse types like Artists)
        if (string.IsNullOrEmpty(item.MetadataHash) && !hasPoster && item.Type != MediaType.Artist)
            return true;

        if (!strictMode)
        {
            // Relaxed: poster alone is sufficient (current behavior)
            return !hasPoster;
        }

        // Strict: type-aware completeness checks
        bool hasDescription = !string.IsNullOrEmpty(item.Overview);

        return item.Type switch
        {
            // Movies/Series: require poster AND description
            MediaType.Movie or MediaType.Series => !hasPoster || !hasDescription,

            // Albums: require poster URL or cover art file on disk
            MediaType.Album => !hasPoster,

            // Artists: always considered complete (metadata is sparse by nature)
            MediaType.Artist => false,

            // Books: require poster AND (author/director or studio/publisher)
            MediaType.Book => !hasPoster || (string.IsNullOrEmpty(item.Director)
                                          && string.IsNullOrEmpty(item.Studio)),

            // Comics are handled earlier (short-circuit on MetadataHash) since
            // their cover/metadata model differs fundamentally from other book types.

            // Default (Games, Photos, Audio): poster is sufficient
            _ => !hasPoster,
        };
    }
}
