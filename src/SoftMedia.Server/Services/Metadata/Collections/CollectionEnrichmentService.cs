using Microsoft.EntityFrameworkCore;
using SoftMedia.Server.Data;
using SoftMedia.Server.Models;
using SoftMedia.Server.Services.Abstractions;

namespace SoftMedia.Server.Services.Metadata.Collections;

public interface ICollectionEnrichmentService
{
    /// <summary>
    /// Wave E2 — invoked at the end of metadata enrichment for a movie. If
    /// the item has an IMDb ID and we haven't yet attempted resolution,
    /// queries Wikidata for the parent series and either attaches the item
    /// to an existing Collection row or creates one.
    ///
    /// Sets <see cref="MediaItem.CollectionLookupAttempted"/> so a re-scan
    /// won't re-query. Manual collection assignments are preserved.
    /// </summary>
    Task EnrichMovieCollectionAsync(MediaItem item, CancellationToken cancellationToken = default);
}

public class CollectionEnrichmentService : ICollectionEnrichmentService
{
    private readonly AppDbContext _db;
    private readonly WikidataCollectionResolver _resolver;
    private readonly ISettingsService _settings;
    private readonly ILogger<CollectionEnrichmentService> _logger;

    public CollectionEnrichmentService(
        AppDbContext db,
        WikidataCollectionResolver resolver,
        ISettingsService settings,
        ILogger<CollectionEnrichmentService> logger)
    {
        _db = db;
        _resolver = resolver;
        _settings = settings;
        _logger = logger;
    }

    public async Task EnrichMovieCollectionAsync(MediaItem item, CancellationToken cancellationToken = default)
    {
        // Skip non-movies. The schema permits CollectionId on any MediaItem
        // because future extensions (e.g. TV-show universes) may want it,
        // but v1 only auto-populates for movies.
        if (item.Type != MediaType.Movie) return;

        // Skip if user opted out.
        var enabled = await _settings.GetSettingAsync("EnableWikidataCollectionLookup", "true");
        if (!string.Equals(enabled, "true", StringComparison.OrdinalIgnoreCase)) return;

        // Skip if there is no IMDb ID — without it we have no way to resolve
        // the Wikidata entity.
        if (string.IsNullOrEmpty(item.ImdbId)) return;

        // Skip if we've already attempted (sentinel pattern, mirrors the
        // comic provider's "EMPTY" hash sentinel).
        if (item.CollectionLookupAttempted.HasValue) return;

        // Manual-collection guard: if the movie is already attached to a
        // collection that has no WikidataId (admin-curated), do not overwrite.
        if (item.CollectionId.HasValue)
        {
            var existing = await _db.Collections
                .AsNoTracking()
                .FirstOrDefaultAsync(c => c.Id == item.CollectionId.Value, cancellationToken);
            if (existing != null && string.IsNullOrEmpty(existing.WikidataId))
            {
                // Manual assignment wins; mark attempted so we don't re-resolve.
                item.CollectionLookupAttempted = true;
                return;
            }
        }

        var lookup = await _resolver.ResolveByImdbIdAsync(item.ImdbId, cancellationToken);
        if (lookup == null)
        {
            // Movie is in no series — record the attempt so we don't retry.
            item.CollectionLookupAttempted = false;
            return;
        }

        // Upsert the auto-collection by WikidataId.
        var collection = await _db.Collections
            .FirstOrDefaultAsync(c => c.WikidataId == lookup.WikidataId, cancellationToken);

        if (collection == null)
        {
            collection = new Collection
            {
                Name = lookup.Name,
                WikidataId = lookup.WikidataId,
                PosterUrl = lookup.PosterUrl,
            };
            _db.Collections.Add(collection);
            await _db.SaveChangesAsync(cancellationToken);
            _logger.LogInformation(
                "[CollectionEnrichment] Created auto-collection {Name} ({Qid})",
                collection.Name, collection.WikidataId);
        }
        else
        {
            // Refresh the canonical name and poster from Wikidata in case the
            // entity label has been updated upstream. Manual collections never
            // hit this branch because they have null WikidataId.
            var changed = false;
            if (!string.Equals(collection.Name, lookup.Name, StringComparison.Ordinal))
            {
                collection.Name = lookup.Name;
                changed = true;
            }
            if (!string.Equals(collection.PosterUrl, lookup.PosterUrl, StringComparison.Ordinal))
            {
                collection.PosterUrl = lookup.PosterUrl;
                changed = true;
            }
            if (changed)
            {
                collection.UpdatedAt = DateTime.UtcNow;
            }
        }

        item.CollectionId = collection.Id;
        item.CollectionLookupAttempted = true;
    }
}
