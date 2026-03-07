using Microsoft.Extensions.Logging;
using SoftMedia.Server.Models;
using SoftMedia.Server.Services.Abstractions;

namespace SoftMedia.Server.Services.Metadata;

public interface IMusicMetadataResolver
{
    Task<MetadataResult?> ResolveMetadataAsync(MediaItem item);
}

public class MusicMetadataResolver : IMusicMetadataResolver
{
    private readonly IEnumerable<IMetadataProvider> _providers;
    private readonly ISettingsService _settingsService;
    private readonly ILogger<MusicMetadataResolver> _logger;

    public MusicMetadataResolver(
        IEnumerable<IMetadataProvider> providers,
        ISettingsService settingsService,
        ILogger<MusicMetadataResolver> logger)
    {
        _providers = providers;
        _settingsService = settingsService;
        _logger = logger;
    }

    public async Task<MetadataResult?> ResolveMetadataAsync(MediaItem item)
    {
        var musicProviderSetting = await _settingsService.GetSettingAsync("MusicProvider", "MusicBrainz");

        var musicProviders = _providers.Where(p => p.SupportedType == LibraryType.Music).ToList();
        var embeddedProvider = musicProviders.FirstOrDefault(p => p.ProviderName == "Embedded");
        var musicBrainzProvider = musicProviders.FirstOrDefault(p => p.ProviderName == "MusicBrainz");

        IMetadataProvider? primary = null;
        IMetadataProvider? fallback = null;

        if (musicProviderSetting == "Embedded")
        {
            primary = embeddedProvider;
        }
        else if (musicProviderSetting == "MusicBrainzOnly")
        {
            primary = musicBrainzProvider;
        }
        else // "MusicBrainz" (default: Embedded primary + MusicBrainz fallback)
        {
            primary = embeddedProvider;
            fallback = musicBrainzProvider;
        }

        MetadataResult? primaryData = null;
        if (primary != null)
        {
            try 
            {
                primaryData = await primary.FetchMetadataAsync(item);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fetching from {Provider}", primary.ProviderName);
            }
        }

        // Check sufficiency
        bool sufficient = false;
        if (primaryData != null)
        {
            if (!string.IsNullOrEmpty(primaryData.Title) && !string.IsNullOrEmpty(primaryData.Artist))
            {
                if (primaryData.HasEmbeddedArt || !string.IsNullOrEmpty(primaryData.PosterUrl))
                {
                    sufficient = true;
                }
            }
        }

        if (sufficient || fallback == null)
        {
            return primaryData;
        }

        // Fetch Fallback
        MetadataResult? fallbackData = null;
        try 
        {
            fallbackData = await fallback.FetchMetadataAsync(item);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching from {Provider}", fallback.ProviderName);
        }

        // Merge Strategy
        var mergedData = primaryData ?? new MetadataResult();
        
        if (fallbackData != null)
        {
            if (primaryData == null)
            {
                mergedData = fallbackData;
            }
            else
            {
                if (string.IsNullOrEmpty(mergedData.PosterUrl)) mergedData.PosterUrl = fallbackData.PosterUrl;
                if (!mergedData.Year.HasValue) mergedData.Year = fallbackData.Year;
                if (string.IsNullOrEmpty(mergedData.Album)) mergedData.Album = fallbackData.Album;
                if (mergedData.Genres == null || mergedData.Genres.Count == 0) mergedData.Genres = fallbackData.Genres;
                if (string.IsNullOrEmpty(mergedData.Title)) mergedData.Title = fallbackData.Title;
                if (string.IsNullOrEmpty(mergedData.Artist)) mergedData.Artist = fallbackData.Artist;
            }
        }

        if (!string.IsNullOrEmpty(mergedData.Title) || !string.IsNullOrEmpty(mergedData.Artist) || mergedData.Year.HasValue)
        {
            return mergedData;
        }

        return null;
    }
}
