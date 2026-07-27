using SoftMedia.Server.Models;
using SoftMedia.Server.Helpers;
using System.Text.Json;
using SoftMedia.Server.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SoftMedia.Server.Services.Abstractions;
using SoftMedia.Server.Services.Media;
using SoftMedia.Server.Services.Metadata.Collections;

namespace SoftMedia.Server.Services.Metadata;

public interface IMetadataAggregator
{
    Task EnrichMediaItemAsync(MediaItem item, LibraryType type, bool deferImageCaching = false, bool refreshImages = true);
}

public class MetadataAggregator : IMetadataAggregator
{
    private readonly IEnumerable<IMetadataProvider> _providers;
    private readonly IMetadataRouter _metadataRouter;
    private readonly ISettingsService _settingsService;
    private readonly IImageUrlExtractorService _imageUrlExtractor;
    private readonly ITvMetadataEnricher _tvMetadataEnricher;
    private readonly ICollectionEnrichmentService _collectionEnrichment;
    private readonly AppDbContext _dbContext;
    private readonly IImageCacheService _imageCache;
    private readonly ILogger<MetadataAggregator> _logger;

    public MetadataAggregator(
        IEnumerable<IMetadataProvider> providers,
        IMetadataRouter metadataRouter,
        ISettingsService settingsService,
        IImageUrlExtractorService imageUrlExtractor,
        ITvMetadataEnricher tvMetadataEnricher,
        ICollectionEnrichmentService collectionEnrichment,
        AppDbContext dbContext,
        IImageCacheService imageCache,
        ILogger<MetadataAggregator> logger)
    {
        _providers = providers;
        _metadataRouter = metadataRouter;
        _settingsService = settingsService;
        _imageUrlExtractor = imageUrlExtractor;
        _tvMetadataEnricher = tvMetadataEnricher;
        _collectionEnrichment = collectionEnrichment;
        _dbContext = dbContext;
        _imageCache = imageCache;
        _logger = logger;
    }

    public async Task EnrichMediaItemAsync(MediaItem item, LibraryType type, bool deferImageCaching = false, bool refreshImages = true)
    {
        try
        {
            // Use MetadataRouter to get metadata from the user's preferred provider
            var result = await _metadataRouter.FetchMetadataAsync(item, type);
            if (result == null)
            {
                // For comics, providers frequently return null for obscure titles
                // (Wikidata doesn't index them; the archive lacks ComicInfo.xml).
                // Mark a sentinel MetadataHash so NeedsEnrichment recognises the
                // attempt and the retry loop can exit. Without this, the retry
                // service spins indefinitely on items no provider can help.
                if (item.Type == MediaType.ComicSeries || item.Type == MediaType.ComicIssue)
                {
                    if (string.IsNullOrEmpty(item.MetadataHash))
                    {
                        item.MetadataHash = "EMPTY";
                        _logger.LogInformation(
                            "[MetadataAggregator] No provider data for comic '{Title}' — marking attempted to break retry loop",
                            item.Title);
                    }
                }
                return;
            }

            await ProcessMetadataResultAsync(item, result, deferImageCaching, refreshImages);

            // Wave E2 — collection auto-population for movies. Runs only when:
            //   - item.Type == Movie (service guards internally),
            //   - EnableWikidataCollectionLookup setting is true,
            //   - item has an IMDb ID and lookup hasn't been attempted yet.
            // The service is failure-tolerant and never throws — collection
            // grouping is non-essential and shouldn't fail enrichment.
            try
            {
                await _collectionEnrichment.EnrichMovieCollectionAsync(item);
            }
            catch (Exception cex)
            {
                _logger.LogWarning(cex, "Collection enrichment failed for {Title}; continuing", item.Title);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error enriching media item {Title}", item.Title);
        }
    }

    private async Task ProcessMetadataResultAsync(MediaItem item, MetadataResult metadata, bool deferImageCaching = false, bool refreshImages = true)
    {
        // Populate standard fields
        if (metadata.Year.HasValue) item.Year = metadata.Year.Value;
        if (!string.IsNullOrEmpty(metadata.Description)) item.Overview = metadata.Description;
        if (!string.IsNullOrEmpty(metadata.ContentRating)) item.ContentRating = metadata.ContentRating;
        
        // Promote Ratings
        if (metadata.ImdbRating.HasValue) item.CommunityRating = metadata.ImdbRating.Value;
        else if (metadata.Rating.HasValue) item.CommunityRating = metadata.Rating.Value;
        
        if (metadata.ReleaseDate.HasValue) item.ReleaseDate = metadata.ReleaseDate.Value;

        // Populate External IDs
        if (!string.IsNullOrEmpty(metadata.ImdbId)) item.ImdbId = metadata.ImdbId;
        if (metadata.TvMazeId.HasValue) item.TvMazeId = metadata.TvMazeId.Value;
        if (!string.IsNullOrEmpty(metadata.MusicBrainzId)) item.MusicBrainzId = metadata.MusicBrainzId;

        // Promote queryable fields
        if (!string.IsNullOrEmpty(metadata.Studio)) item.Studio = metadata.Studio;
        if (!string.IsNullOrEmpty(metadata.Director)) item.Director = metadata.Director;

        // Books express the producing organisation as Publisher. Studio is the shared column
        // for both (see MediaItem.Studio), so a provider that only fills Publisher — as any
        // future book provider reasonably might — still lands here rather than being dropped.
        // OpenLibrary sets both, which is why this is a fallback and not the primary path.
        if (string.IsNullOrEmpty(item.Studio) && !string.IsNullOrEmpty(metadata.Publisher))
            item.Studio = metadata.Publisher;

        // Book identifiers, file-first. The scanner has already stamped whatever the EPUB/PDF
        // itself declared, and that describes the exact edition sitting on disk; a provider
        // result describes the work and may well be a different printing. So these fill only
        // the gaps — an OpenLibrary page count reaches a reflowable EPUB (which has no
        // intrinsic pagination) but never displaces a PDF's real page tree count.
        if (string.IsNullOrEmpty(item.Isbn))
        {
            var normalizedIsbn = IsbnNormalizer.Normalize(metadata.Isbn);
            if (normalizedIsbn != null) item.Isbn = normalizedIsbn;
        }
        if (!item.PageCount.HasValue && metadata.PageCount is > 0)
            item.PageCount = metadata.PageCount;

        // Photos: persist the display-only EXIF dictionary (camera/iso/gps/…) to its JSON
        // column — the only consumer of MetadataResult.Extra that must survive to the DTO
        // (PhotoDetailView). Values arrive as JSON strings; store a flat string map so the
        // DTO parse matches PhotoScanner's inline writes.
        if (item.Type == MediaType.Photo && metadata.Extra is { Count: > 0 })
        {
            var exifFields = metadata.Extra.ToDictionary(
                kv => kv.Key,
                kv => kv.Value.ValueKind == System.Text.Json.JsonValueKind.String
                    ? kv.Value.GetString() ?? kv.Value.ToString()
                    : kv.Value.ToString());
            item.ExifJson = System.Text.Json.JsonSerializer.Serialize(exifFields);
        }

        // Persist normalized genres and cast to relational tables
        if (metadata.Genres != null && metadata.Genres.Count > 0)
            await PersistGenresAsync(item, metadata.Genres);

        if (metadata.Cast != null && metadata.Cast.Count > 0)
            await PersistCastAsync(item, metadata.Cast);

        // Filtering: Only keep metadata for Seasons/Episodes that exist locally
        if (item.Type == MediaType.Series)
        {
            await _tvMetadataEnricher.FilterToLocalEpisodesAsync(item, metadata);
            await _tvMetadataEnricher.PropagateEpisodeMetadataAsync(item, metadata);
            await _tvMetadataEnricher.PropagateSeasonMetadataAsync(item, metadata);
        }

        if (deferImageCaching || !refreshImages)
        {
            return;
        }

        // R-WI-014: an NFO-referenced local poster (already resolved + jailed to the NFO's
        // folder by the NFO provider) is ingested through the cache-copy path under the
        // NFO-distinct key. A scanner-applied SIDECAR (poster.jpg → "…_poster_local") outranks
        // an NFO reference; anything else (provider art, a missing cache file after a DB-only
        // restore, a changed thumb) re-ingests — the exact-freshness check makes the repeat a
        // cheap no-op. This also heals NFO-sourced art after restores (review finding).
        var nfoOwnsPoster = item.PosterFromLocalFile
            && item.PosterUrl != null
            && item.PosterUrl.Contains("_poster_nfo.", StringComparison.OrdinalIgnoreCase);
        var sidecarOwnsPoster = item.PosterFromLocalFile
            && item.PosterUrl != null
            && item.PosterUrl.Contains(Services.Media.LocalArtworkService.SidecarKeySuffix + ".", StringComparison.OrdinalIgnoreCase);
        if (!string.IsNullOrEmpty(metadata.LocalPosterFile) && !sidecarOwnsPoster)
        {
            var cacheKey = item.Type == MediaType.Series ? $"tv/{item.Id}_poster_nfo" : $"movies/{item.Id}_poster_nfo";
            // Jail at the NFO's own folder (travels with the result) — never at the poster
            // file's parent, which a symlinked subdirectory could relocate outside the library.
            var localWebPath = metadata.LocalPosterJailRoot == null ? null
                : await _imageCache.CacheLocalImageAsync(metadata.LocalPosterFile, cacheKey, metadata.LocalPosterJailRoot);
            if (localWebPath != null)
            {
                item.PosterUrl = localWebPath;
                item.PosterFromLocalFile = true;
                _logger.LogInformation("Applied NFO-referenced local poster to {Title}", item.Title);
            }
        }
        else if (string.IsNullOrEmpty(metadata.LocalPosterFile) && nfoOwnsPoster)
        {
            // The NFO no longer references a local poster (thumb removed, NFO deleted, or the
            // provider chain changed): release the local claim so provider art can apply below
            // — without this, the stale flag suppressed provider posters forever and a
            // DB-restore repair looped without healing (verifier finding).
            item.PosterFromLocalFile = false;
            _logger.LogInformation("NFO local poster reference gone for {Title}; provider art re-enabled.", item.Title);
        }

        // R-WI-014: local sidecar art WINS over provider art — while the local-poster flag is
        // set, the provider's poster is neither applied to the item nor queued for download
        // (nulling here suppresses both, since the extractor reads this field). Descriptions,
        // genres, cast etc. above are unaffected — that's the whole point of the local-art flag.
        // (No backdrop equivalent needed: MetadataResult carries no movie/series backdrop.)
        if (item.PosterFromLocalFile) metadata.PosterUrl = null;

        // Capture remote URLs to promoted columns BEFORE image extraction strips them.
        // These columns store the original remote URLs as the source of truth — but only
        // until the art is cached: once ImageDownloadQueueService has written a
        // "/cache/images/…" path here, re-stamping the provider URL on the next enrichment
        // would flip the whole library back onto /api/v1/image/proxy (and re-download art
        // into cache/images/proxy) for as long as it takes the queue to catch up, even
        // though the identical file is already on disk. The extractor reads
        // metadata.PosterUrl, not this column, so the download is still queued — a cache
        // file that has gone missing (DB-only restore, manual wipe) re-downloads under the
        // same key and heals the path in place.
        if (!string.IsNullOrEmpty(metadata.PosterUrl) && !IsCachedArtworkPath(item.PosterUrl))
            item.PosterUrl = metadata.PosterUrl;

        // Flush what we've promoted BEFORE handing the URLs to the background image queue.
        // That queue caches the file and writes "/cache/images/…" back on its OWN DbContext,
        // within milliseconds when the file already exists — and MetadataQueueService's
        // post-enrichment SaveChanges would then overwrite it with the remote URL this
        // context still holds in memory. That lost update is why movie posters stayed
        // proxied forever: movies lose the race by seconds because the Wikidata collection
        // lookup runs between the enqueue below and that final save. Saving here leaves
        // PosterUrl unmodified, so the later save no longer carries the column at all.
        await _dbContext.SaveChangesAsync();

        // Process images (ExtractAndQueueAsync nulls out remote URLs to prevent hotlinking)
        bool imagesEnqueued = await _imageUrlExtractor.ExtractAndQueueAsync(item, metadata);

        // Compute stable MetadataHash from promoted strongly-typed fields.
        // This avoids the old JSON-ordering instability that triggered false-positive
        // "data changed" signals and unnecessary enrichment retries.
        using var sha256 = System.Security.Cryptography.SHA256.Create();
        var hashInput = $"{item.Year}|{item.Overview}|{item.CommunityRating}|{item.ContentRating}|{item.Studio}|{item.Director}|{item.PosterUrl}|{item.BackdropUrl}|{item.ImdbId}|{item.TvMazeId}|{item.MusicBrainzId}|{item.Isbn}|{item.PageCount}";
        var hashBytes = sha256.ComputeHash(System.Text.Encoding.UTF8.GetBytes(hashInput));
        item.MetadataHash = Convert.ToBase64String(hashBytes);

        // Store raw payload in cache for scanners to use (e.g. instant episode naming)
        if (!string.IsNullOrEmpty(metadata.RawPayload))
        {
            var providerName = "TVMaze"; // Currently only TVMaze provides full embedded data
            var existingCache = await _dbContext.ProviderMetadataCaches
                .FirstOrDefaultAsync(c => c.MediaItemId == item.Id && c.ProviderId == providerName);
            
            if (existingCache == null)
            {
                _dbContext.ProviderMetadataCaches.Add(new ProviderMetadataCache
                {
                    MediaItemId = item.Id,
                    ProviderId = providerName,
                    RawPayload = metadata.RawPayload,
                    LastUpdated = DateTime.UtcNow
                });
            }
            else
            {
                existingCache.RawPayload = metadata.RawPayload;
                existingCache.LastUpdated = DateTime.UtcNow;
            }
        }
    }

    /// <summary>
    /// True for artwork this server already holds on disk — the "/cache/images/…" web path
    /// written by ImageDownloadQueueService (provider art) or CacheLocalImageAsync (sidecar
    /// art). Anything else (a provider http(s) URL) is served through the image proxy.
    /// </summary>
    private static bool IsCachedArtworkPath(string? url) =>
        url != null && url.StartsWith("/cache/images/", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Persist genre data to the normalized Genre/MediaItemGenre tables.
    /// Uses batch loading and diff-based association updates to avoid N+1 writes.
    /// </summary>
    private async Task PersistGenresAsync(MediaItem item, List<string> genreNames)
    {
        try
        {
            // Canonicalise before touching the DB: splits BISAC subject paths that
            // book providers send as one string ("FICTION / Science Fiction / Space
            // Opera"), drops non-genre subject headings ("Dune (Imaginary place)",
            // bare years), and unifies casing so music tags and video genres share
            // rows. See GenreNormalizer for the full rationale.
            var canonicalNames = GenreNormalizer.NormalizeAll(genreNames);

            if (canonicalNames.Count == 0) return;

            // Match case-INSENSITIVELY against existing rows. SQLite's default BINARY
            // collation makes both `IN (...)` and the UNIQUE index on Name
            // case-sensitive, so comparing raw names let "Science Fiction" fail to
            // find an existing "science fiction" and insert a duplicate — which is
            // exactly how three spellings of it accumulated. Comparing on lowered
            // values is what actually de-duplicates; the Genre table is small enough
            // (hundreds of rows) that losing the index on this lookup is irrelevant.
            var lowered = canonicalNames.Select(n => n.ToLowerInvariant()).ToList();
            var existingRows = await _dbContext.Genres
                .Where(g => lowered.Contains(g.Name.ToLower()))
                .ToListAsync();

            // Tolerate case-duplicates still present in the table (this runs whether
            // or not the one-off merge has happened yet): collapse to the lowest Id
            // per name instead of throwing on a duplicate dictionary key.
            var existingGenres = existingRows
                .GroupBy(g => g.Name, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    grp => grp.Key,
                    grp => grp.OrderBy(g => g.Id).First(),
                    StringComparer.OrdinalIgnoreCase);

            // Batch-create any missing Genre entities
            var newGenres = new List<Genre>();
            foreach (var name in canonicalNames)
            {
                if (!existingGenres.ContainsKey(name))
                {
                    var genre = new Genre { Name = name };
                    newGenres.Add(genre);
                    existingGenres[name] = genre;
                }
            }

            if (newGenres.Count > 0)
            {
                _dbContext.Genres.AddRange(newGenres);
                await _dbContext.SaveChangesAsync();
            }

            // Diff-based association update: compute delta instead of delete-and-rebuild
            var existingAssociations = await _dbContext.MediaItemGenres
                .Where(mg => mg.MediaItemId == item.Id)
                .ToListAsync();

            var existingGenreIds = existingAssociations.Select(a => a.GenreId).ToHashSet();
            var desiredGenreIds = canonicalNames
                .Select(name => existingGenres[name].Id)
                .ToHashSet();

            // Remove stale associations
            var toRemove = existingAssociations.Where(a => !desiredGenreIds.Contains(a.GenreId)).ToList();
            if (toRemove.Count > 0)
                _dbContext.MediaItemGenres.RemoveRange(toRemove);

            // Add missing associations
            var toAdd = desiredGenreIds.Except(existingGenreIds)
                .Select(genreId => new MediaItemGenre { MediaItemId = item.Id, GenreId = genreId })
                .ToList();
            if (toAdd.Count > 0)
                _dbContext.MediaItemGenres.AddRange(toAdd);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist genres for {Title}", item.Title);
        }
    }

    /// <summary>
    /// Persist cast data to the normalized Person/MediaItemCast tables.
    /// Uses batch loading, external ID dedup, and diff-based association updates.
    /// </summary>
    private async Task PersistCastAsync(MediaItem item, List<CastMember> castMembers)
    {
        try
        {
            var validMembers = castMembers
                .Where(m => !string.IsNullOrEmpty(m.Name))
                .ToList();

            if (validMembers.Count == 0) return;

            // Batch-load all potentially matching Person entities
            var memberNames = validMembers.Select(m => m.Name).Distinct().ToList();
            var memberExternalIds = validMembers
                .Where(m => m.Id.HasValue && m.Id.Value > 0)
                .Select(m => m.Id!.Value)
                .Distinct()
                .ToList();

            var personsByExternalId = memberExternalIds.Count > 0
                ? await _dbContext.Persons
                    .Where(p => p.ExternalId.HasValue && memberExternalIds.Contains(p.ExternalId.Value))
                    .ToDictionaryAsync(p => p.ExternalId!.Value)
                : new Dictionary<int, Person>();

            var personsByName = await _dbContext.Persons
                .Where(p => memberNames.Contains(p.Name))
                .ToDictionaryAsync(p => p.Name);

            // Resolve or create Person entities in batch
            var newPersons = new List<Person>();
            var resolvedPersons = new List<(Person Person, CastMember Member, int Order)>();

            for (int i = 0; i < validMembers.Count; i++)
            {
                var member = validMembers[i];
                Person? person = null;

                // Prefer ExternalId dedup, fall back to name
                if (member.Id.HasValue && member.Id.Value > 0)
                    personsByExternalId.TryGetValue(member.Id.Value, out person);

                person ??= personsByName.GetValueOrDefault(member.Name);

                if (person == null)
                {
                    person = new Person
                    {
                        Name = member.Name,
                        ExternalId = member.Id,
                        ImagePath = member.ImageUrl
                    };
                    newPersons.Add(person);
                    // Register in lookups to avoid creating duplicates within the same batch
                    personsByName[member.Name] = person;
                    if (member.Id.HasValue && member.Id.Value > 0)
                        personsByExternalId[member.Id.Value] = person;
                }
                else if (member.Id.HasValue && !person.ExternalId.HasValue)
                {
                    person.ExternalId = member.Id;
                }

                resolvedPersons.Add((person, member, i));
            }

            if (newPersons.Count > 0)
            {
                _dbContext.Persons.AddRange(newPersons);
                await _dbContext.SaveChangesAsync();
            }

            // Diff-based cast association update (avoids destructive delete-and-rebuild)
            var existingAssociations = await _dbContext.MediaItemCasts
                .Where(mc => mc.MediaItemId == item.Id)
                .OrderBy(mc => mc.Order)
                .ToListAsync();

            var newEntries = new List<MediaItemCast>();

            for (int i = 0; i < resolvedPersons.Count; i++)
            {
                var rp = resolvedPersons[i];
                var character = string.IsNullOrWhiteSpace(rp.Member.Character) ? null : rp.Member.Character;

                if (i < existingAssociations.Count)
                {
                    // Map over existing entity, preserving the surrogate Id Key
                    var existing = existingAssociations[i];
                    existing.PersonId = rp.Person.Id;
                    existing.Character = character;
                    existing.Order = rp.Order;
                }
                else
                {
                    // Append new entity
                    newEntries.Add(new MediaItemCast
                    {
                        MediaItemId = item.Id,
                        PersonId = rp.Person.Id,
                        Character = character,
                        Order = rp.Order
                    });
                }
            }

            // Remove any trailing stale associations
            if (existingAssociations.Count > resolvedPersons.Count)
            {
                var toRemove = existingAssociations.Skip(resolvedPersons.Count).ToList();
                _dbContext.MediaItemCasts.RemoveRange(toRemove);
            }

            if (newEntries.Count > 0)
            {
                _dbContext.MediaItemCasts.AddRange(newEntries);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist cast for {Title}", item.Title);
        }
    }
}
