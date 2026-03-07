using SoftMedia.Server.Models;
using SoftMedia.Server.Helpers;
using System.Text.Json;
using SoftMedia.Server.Data;
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
    private readonly ILogger<MetadataAggregator> _logger;

    public MetadataAggregator(
        IEnumerable<IMetadataProvider> providers,
        IMetadataRouter metadataRouter,
        ISettingsService settingsService, 
        IImageUrlExtractorService imageUrlExtractor,
        ITvMetadataEnricher tvMetadataEnricher,
        IMusicMetadataResolver musicMetadataResolver,
        ILogger<MetadataAggregator> logger)
    {
        _providers = providers;
        _metadataRouter = metadataRouter;
        _settingsService = settingsService;
        _imageUrlExtractor = imageUrlExtractor;
        _tvMetadataEnricher = tvMetadataEnricher;
        _musicMetadataResolver = musicMetadataResolver;
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
                        
                        // Re-serialize
                        json = JsonSerializer.Serialize(existing);
                        
                        // Deserialize back into MetadataResult so downstream pipeline has updated info
                        result = JsonSerializer.Deserialize<MetadataResult>(json, jsonOptions) ?? result;
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

        // Promote queryable fields
        if (metadata.Genres != null && metadata.Genres.Count > 0) item.Genres = string.Join(", ", metadata.Genres);
        if (!string.IsNullOrEmpty(metadata.Studio)) item.Studio = metadata.Studio;
        if (!string.IsNullOrEmpty(metadata.Director)) item.Director = metadata.Director;

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

        // Update MetadataJson to reflect removed remote URLs
        var jsonOptions = new JsonSerializerOptions 
        { 
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        };
        item.MetadataJson = JsonSerializer.Serialize(metadata, jsonOptions);
    }
}
