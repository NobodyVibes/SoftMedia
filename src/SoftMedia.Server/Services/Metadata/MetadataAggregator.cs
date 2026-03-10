using SoftMedia.Server.Models;
using SoftMedia.Server.Helpers;
using System.Text.Json;
using SoftMedia.Server.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SoftMedia.Server.Services.Abstractions;
using SoftMedia.Server.Services.Media;

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
    private readonly IMusicMetadataResolver _musicMetadataResolver;
    private readonly AppDbContext _dbContext;
    private readonly ILogger<MetadataAggregator> _logger;

    public MetadataAggregator(
        IEnumerable<IMetadataProvider> providers,
        IMetadataRouter metadataRouter,
        ISettingsService settingsService, 
        IImageUrlExtractorService imageUrlExtractor,
        ITvMetadataEnricher tvMetadataEnricher,
        IMusicMetadataResolver musicMetadataResolver,
        AppDbContext dbContext,
        ILogger<MetadataAggregator> logger)
    {
        _providers = providers;
        _metadataRouter = metadataRouter;
        _settingsService = settingsService;
        _imageUrlExtractor = imageUrlExtractor;
        _tvMetadataEnricher = tvMetadataEnricher;
        _musicMetadataResolver = musicMetadataResolver;
        _dbContext = dbContext;
        _logger = logger;
    }

    public async Task EnrichMediaItemAsync(MediaItem item, LibraryType type, bool deferImageCaching = false, bool refreshImages = true)
    {
        try
        {
            var jsonOptions = new JsonSerializerOptions 
            { 
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
            };

            MetadataResult? result = null;

            if (type == LibraryType.Music)
            {
                result = await _musicMetadataResolver.ResolveMetadataAsync(item);
                if (result != null)
                {
                    item.MetadataJson = JsonSerializer.Serialize(result, jsonOptions);
                    await ProcessMetadataResultAsync(item, result, deferImageCaching, refreshImages);
                }
                return;
            }

            // Use MetadataRouter to get metadata from the user's preferred provider
            result = await _metadataRouter.FetchMetadataAsync(item, type);
            if (result == null) return;

            var json = JsonSerializer.Serialize(result, jsonOptions);

            // MERGE Logic: Preserve existing technical metadata (chapters, creditsStart)
            if (!string.IsNullOrEmpty(item.MetadataJson))
            {
                try
                {
                    var existing = MetadataJsonHelper.Parse(item.MetadataJson);
                    var newMeta = JsonSerializer.Deserialize<Dictionary<string, object>>(json);

                    if (existing != null && newMeta != null)
                    {
                        // Update existing with new values (External metadata wins for promoted fields)
                        foreach (var kvp in newMeta)
                        {
                            existing[kvp.Key] = kvp.Value;
                        }
                        
                        // Re-serialize merged dictionary — result object already holds correct API data
                        json = JsonSerializer.Serialize(existing);
                    }
                }
                catch
                {
                    // If merge fails, fall back to overwrite but log warning
                    _logger.LogWarning("Failed to merge metadata for {Title}, overwriting.", item.Title);
                }
            }

            item.MetadataJson = json;

            await ProcessMetadataResultAsync(item, result, deferImageCaching, refreshImages);
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

        // Promote queryable fields (keep comma-separated string for backward compatibility)
        if (metadata.Genres != null && metadata.Genres.Count > 0) item.Genres = string.Join(", ", metadata.Genres);
        if (!string.IsNullOrEmpty(metadata.Studio)) item.Studio = metadata.Studio;
        if (!string.IsNullOrEmpty(metadata.Director)) item.Director = metadata.Director;

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
        }

        if (deferImageCaching || !refreshImages)
        {
            return;
        }

        // Process images
        bool imagesEnqueued = await _imageUrlExtractor.ExtractAndQueueAsync(item, metadata);

        // Re-serialize the MetadataResult (with image URLs now stripped) and MERGE
        // into the existing MetadataJson to preserve scanner-injected keys (e.g. "author"
        // from BookScanner, "hasEmbeddedArt" from IMediaAnalysisService).
        var jsonOptions = new JsonSerializerOptions 
        { 
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        };
        var updatedResultJson = JsonSerializer.Serialize(metadata, jsonOptions);

        if (!string.IsNullOrEmpty(item.MetadataJson))
        {
            try
            {
                var existing = MetadataJsonHelper.Parse(item.MetadataJson);
                var updated = JsonSerializer.Deserialize<Dictionary<string, object>>(updatedResultJson);

                if (existing != null && updated != null)
                {
                    // Merge: updated values (with stripped URLs) win, but existing keys are preserved
                    foreach (var kvp in updated)
                    {
                        existing[kvp.Key] = kvp.Value;
                    }
                    item.MetadataJson = JsonSerializer.Serialize(existing);
                }
                else
                {
                    item.MetadataJson = updatedResultJson;
                }
            }
            catch
            {
                item.MetadataJson = updatedResultJson;
            }
        }
        else
        {
            item.MetadataJson = updatedResultJson;
        }
    }

    /// <summary>
    /// Persist genre data to the normalized Genre/MediaItemGenre tables.
    /// Uses "get or create" pattern for Genre entities.
    /// </summary>
    private async Task PersistGenresAsync(MediaItem item, List<string> genreNames)
    {
        try
        {
            // Remove existing genre associations for this item
            var existingAssociations = await _dbContext.MediaItemGenres
                .Where(mg => mg.MediaItemId == item.Id)
                .ToListAsync();
            _dbContext.MediaItemGenres.RemoveRange(existingAssociations);

            foreach (var genreName in genreNames)
            {
                var trimmedName = genreName.Trim();
                if (string.IsNullOrEmpty(trimmedName)) continue;

                // Get or create Genre entity
                var genre = await _dbContext.Genres
                    .FirstOrDefaultAsync(g => g.Name == trimmedName);

                if (genre == null)
                {
                    genre = new Genre { Name = trimmedName };
                    _dbContext.Genres.Add(genre);
                    await _dbContext.SaveChangesAsync();
                }

                // Create junction entry (ignore duplicates)
                var exists = await _dbContext.MediaItemGenres
                    .AnyAsync(mg => mg.MediaItemId == item.Id && mg.GenreId == genre.Id);

                if (!exists)
                {
                    _dbContext.MediaItemGenres.Add(new MediaItemGenre
                    {
                        MediaItemId = item.Id,
                        GenreId = genre.Id
                    });
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist genres for {Title}", item.Title);
        }
    }

    /// <summary>
    /// Persist cast data to the normalized Person/MediaItemCast tables.
    /// Uses external ID for deduplication when available, falls back to name matching.
    /// </summary>
    private async Task PersistCastAsync(MediaItem item, List<CastMember> castMembers)
    {
        try
        {
            // Remove existing cast associations for this item
            var existingAssociations = await _dbContext.MediaItemCasts
                .Where(mc => mc.MediaItemId == item.Id)
                .ToListAsync();
            _dbContext.MediaItemCasts.RemoveRange(existingAssociations);

            for (int i = 0; i < castMembers.Count; i++)
            {
                var member = castMembers[i];
                if (string.IsNullOrEmpty(member.Name)) continue;

                // Get or create Person entity (prefer ExternalId dedup, fall back to name)
                Person? person = null;

                if (member.Id.HasValue && member.Id.Value > 0)
                {
                    person = await _dbContext.Persons
                        .FirstOrDefaultAsync(p => p.ExternalId == member.Id.Value);
                }

                person ??= await _dbContext.Persons
                    .FirstOrDefaultAsync(p => p.Name == member.Name);

                if (person == null)
                {
                    person = new Person
                    {
                        Name = member.Name,
                        ExternalId = member.Id,
                        ImagePath = member.ImageUrl
                    };
                    _dbContext.Persons.Add(person);
                    await _dbContext.SaveChangesAsync();
                }
                else if (member.Id.HasValue && !person.ExternalId.HasValue)
                {
                    // Update existing person with external ID if we now have it
                    person.ExternalId = member.Id;
                }

                _dbContext.MediaItemCasts.Add(new MediaItemCast
                {
                    MediaItemId = item.Id,
                    PersonId = person.Id,
                    Character = member.Character,
                    Order = i
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist cast for {Title}", item.Title);
        }
    }
}
