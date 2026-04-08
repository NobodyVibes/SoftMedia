using System.Text.Json;
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
    /// complete if it has a poster URL with a non-null value. This preserves existing behavior.
    /// </para>
    /// <para>
    /// <b>Strict mode (<paramref name="strictMode"/> = true):</b> Checks type-specific
    /// required fields beyond just a poster. Movies/Series also need a description.
    /// Books also need an author or publisher. Albums need poster or cover art on disk.
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

        // No metadata at all — definitely needs enrichment
        if (string.IsNullOrEmpty(item.MetadataJson))
            return true;

        try
        {
            using var doc = JsonDocument.Parse(item.MetadataJson);
            var root = doc.RootElement;
            
            bool hasPoster = HasKey(root, "poster");

            if (!strictMode)
            {
                // Relaxed: poster alone is sufficient (current behavior)
                return !hasPoster;
            }

            // Strict: type-aware completeness checks
            bool hasDescription = HasKey(root, "description")
                               || !string.IsNullOrEmpty(item.Overview);

            return item.Type switch
            {
                // Movies/Series: require poster AND description
                MediaType.Movie or MediaType.Series => !hasPoster || !hasDescription,

                // Albums: require poster URL or cover art file on disk
                MediaType.Album => !hasPoster && string.IsNullOrEmpty(item.CoverArtPath),

                // Artists: require a title in MetadataJson (metadata is sparse by nature)
                MediaType.Artist => !HasKey(root, "title"),

                // Books: require poster AND (author/cast or publisher)
                MediaType.Book => !hasPoster || (!HasKey(root, "cast")
                                              && !HasKey(root, "publisher")),

                // Default (Games, Photos, Audio): poster is sufficient
                _ => !hasPoster,
            };
        }
        catch (JsonException)
        {
            return true; // If JSON is invalid, it needs enrichment
        }
    }

    /// <summary>
    /// Checks whether a JSON element contains a specific key with a non-null value.
    /// </summary>
    private static bool HasKey(JsonElement root, string key)
    {
        if (root.TryGetProperty(key, out var value))
        {
            return value.ValueKind != JsonValueKind.Null &&
                   value.ValueKind != JsonValueKind.Undefined;
        }
        return false;
    }
}
