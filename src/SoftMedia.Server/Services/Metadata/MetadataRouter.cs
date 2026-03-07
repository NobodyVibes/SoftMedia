using SoftMedia.Server.Models;

namespace SoftMedia.Server.Services.Metadata;

public interface IMetadataRouter
{
    Task<MetadataResult?> FetchMetadataAsync(MediaItem item, LibraryType type);
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

    public async Task<MetadataResult?> FetchMetadataAsync(MediaItem item, LibraryType type)
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

        // 2. Find the matching provider
        var provider = _providers.FirstOrDefault(p => p.SupportedType == type && p.ProviderName == preferredProvider)
                       ?? _providers.FirstOrDefault(p => p.SupportedType == type); // Fallback to any provider for type

        if (provider == null)
            return null;

        // 3. Handle keyed providers (API key management)
        if (provider is IKeyedMetadataProvider keyedProvider)
        {
            return await FetchKeyedMetadataAsync(keyedProvider, item);
        }

        return await provider.FetchMetadataAsync(item);
    }

    /// <summary>
    /// Handles metadata fetching for providers that require API key management.
    /// </summary>
    private async Task<MetadataResult?> FetchKeyedMetadataAsync(IKeyedMetadataProvider keyedProvider, MediaItem item)
    {
        // Resolve key settings using the provider name as prefix
        var keyModeSettingKey = $"{keyedProvider.ProviderName}ApiKeyMode";
        var customKeySettingKey = $"{keyedProvider.ProviderName}ApiKeyCustom";

        var keyMode = await _settingsService.GetSettingAsync(keyModeSettingKey, "softmedia");
        var customKey = await _settingsService.GetSettingAsync(customKeySettingKey, "");

        var apiKey = keyedProvider.GetActiveApiKey(keyMode, customKey);

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            if (keyMode == "disabled")
            {
                _logger.LogDebug("{Provider} is disabled for: {Title}", keyedProvider.ProviderName, item.Title);
            }
            else
            {
                _logger.LogWarning("No valid API key configured for {Provider}. Mode: {Mode}", 
                    keyedProvider.ProviderName, keyMode);
            }
            return null;
        }

        return await keyedProvider.FetchMetadataWithKeyAsync(item, apiKey, keyMode);
    }
}

