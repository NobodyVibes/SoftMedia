using SoftMedia.Server.Models;

namespace SoftMedia.Server.Services.Metadata;

public interface IMetadataRouter
{
    Task<string?> FetchMetadataAsync(MediaItem item, LibraryType type);
}

public class MetadataRouter : IMetadataRouter
{
    private readonly IEnumerable<IMetadataProvider> _providers;
    private readonly ISettingsService _settingsService;
    private readonly ILogger<MetadataRouter> _logger;

    public MetadataRouter(
        IEnumerable<IMetadataProvider> providers, 
        ISettingsService settingsService,
        ILogger<MetadataRouter> logger)
    {
        _providers = providers;
        _settingsService = settingsService;
        _logger = logger;
    }

    public async Task<string?> FetchMetadataAsync(MediaItem item, LibraryType type)
    {
        // 1. Determine which provider to use based on settings
        string? preferredProvider = null;
        switch (type)
        {
            case LibraryType.Movie:
                preferredProvider = await _settingsService.GetSettingAsync("MovieProvider", "Wikidata");
                break;
            case LibraryType.TV:
                preferredProvider = await _settingsService.GetSettingAsync("TVProvider", "TVMaze");
                break;
            case LibraryType.Music:
                preferredProvider = await _settingsService.GetSettingAsync("MusicProvider", "MusicBrainz");
                break;
            case LibraryType.Book:
                preferredProvider = await _settingsService.GetSettingAsync("BookProvider", "Open Library");
                break;
            case LibraryType.Game:
                preferredProvider = await _settingsService.GetSettingAsync("GameProvider", "Wikidata");
                break;
            case LibraryType.Photo:
                preferredProvider = await _settingsService.GetSettingAsync("PhotoProvider", "Exif");
                break;
        }

        _logger.LogInformation("Using metadata provider '{Provider}' for {Type}: {Title}", 
            preferredProvider, type, item.Title);

        // 2. Special handling for OMDB (requires API key)
        if (type == LibraryType.Movie && preferredProvider == "OMDb")
        {
            return await FetchOMDbMetadataAsync(item);
        }

        // 3. Find the matching provider
        var provider = _providers.FirstOrDefault(p => p.SupportedType == type && p.ProviderName == preferredProvider)
                       ?? _providers.FirstOrDefault(p => p.SupportedType == type); // Fallback to any provider for type

        if (provider != null)
        {
            return await provider.FetchMetadataAsync(item);
        }
        return null;
    }

    /// <summary>
    /// Handles OMDB metadata fetching with API key management.
    /// </summary>
    private async Task<string?> FetchOMDbMetadataAsync(MediaItem item)
    {
        var omdbProvider = _providers.OfType<OMDbProvider>().FirstOrDefault();
        if (omdbProvider == null)
        {
            _logger.LogWarning("OMDB provider not registered");
            return null;
        }

        // Get API key mode and custom key
        var keyMode = await _settingsService.GetSettingAsync("OMDbApiKeyMode", "softmedia");
        var customKey = await _settingsService.GetSettingAsync("OMDbApiKeyCustom", "");

        // Resolve the actual API key
        var apiKey = omdbProvider.GetActiveApiKey(keyMode, customKey);

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            if (keyMode == "disabled")
            {
                _logger.LogDebug("OMDB is disabled for movie: {Title}", item.Title);
            }
            else
            {
                _logger.LogWarning("No valid OMDB API key configured. Mode: {Mode}", keyMode);
            }
            return null;
        }

        return await omdbProvider.FetchMetadataWithKeyAsync(item, apiKey, keyMode);
    }
}

