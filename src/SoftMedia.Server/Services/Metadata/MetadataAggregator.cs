using SoftMedia.Server.Models;
using System.Text.Json;

namespace SoftMedia.Server.Services.Metadata;

public class MetadataAggregator
{
    private readonly IEnumerable<IMetadataProvider> _providers;
    private readonly ILogger<MetadataAggregator> _logger;

    public MetadataAggregator(IEnumerable<IMetadataProvider> providers, ILogger<MetadataAggregator> logger)
    {
        _providers = providers;
        _logger = logger;
    }

    public async Task EnrichMediaItemAsync(MediaItem item, LibraryType type)
    {
        var provider = _providers.FirstOrDefault(p => p.SupportedType == type);
        if (provider == null) return;

        try
        {
            var json = await provider.FetchMetadataAsync(item.Title, item.Path);
            if (string.IsNullOrEmpty(json)) return;

            item.MetadataJson = json;

            // Parse and promote fields
            var metadata = JsonSerializer.Deserialize<Dictionary<string, object>>(json);
            if (metadata != null)
            {
                if (metadata.TryGetValue("year", out var yearObj) && int.TryParse(yearObj.ToString(), out var year))
                {
                    item.Year = year;
                }

                if (metadata.TryGetValue("description", out var descObj))
                {
                    item.Overview = descObj.ToString();
                }
                
                if (metadata.TryGetValue("rating", out var ratingObj) && double.TryParse(ratingObj.ToString(), out var rating))
                {
                    item.CommunityRating = rating;
                }
                
                 if (metadata.TryGetValue("contentRating", out var contentRatingObj))
                {
                    item.ContentRating = contentRatingObj.ToString();
                }
                 
                 if (metadata.TryGetValue("releaseDate", out var releaseDateObj) && DateTime.TryParse(releaseDateObj.ToString(), out var releaseDate))
                {
                    item.ReleaseDate = releaseDate;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error enriching media item {Title}", item.Title);
        }
    }
}
