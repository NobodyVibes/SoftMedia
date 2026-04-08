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
        // Music has its own routing logic (primary + fallback + sufficiency check)
        if (type == LibraryType.Music)
        {
            return await FetchMusicMetadataAsync(item);
        }

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
    /// Music-specific routing: primary + fallback strategy with sufficiency check.
    /// Modes: "Embedded" (embedded only), "MusicBrainzOnly" (MusicBrainz only),
    /// "MusicBrainz" (default: Embedded primary, MusicBrainz fallback).
    /// </summary>
    private async Task<MetadataResult?> FetchMusicMetadataAsync(MediaItem item)
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

        _logger.LogInformation("Music routing: Primary={Primary}, Fallback={Fallback} for '{Title}'",
            primary?.ProviderName ?? "none", fallback?.ProviderName ?? "none", item.Title);

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

        // Sufficiency check varies by item type:
        // - Tracks: need title + artist + art
        // - Albums: need title + art (artist is on parent, not in MetadataResult)
        // - Artists: need title (art is optional, often not available from embedded)
        bool sufficient = primaryData != null && !string.IsNullOrEmpty(primaryData.Title);
        if (sufficient && item.Type == MediaType.Audio)
        {
            sufficient = !string.IsNullOrEmpty(primaryData.Artist)
                && (primaryData.HasEmbeddedArt || !string.IsNullOrEmpty(primaryData.PosterUrl));
        }
        else if (sufficient && item.Type == MediaType.Album)
        {
            sufficient = primaryData.HasEmbeddedArt || !string.IsNullOrEmpty(primaryData.PosterUrl);
        }

        if (sufficient || fallback == null)
        {
            return primaryData;
        }

        // Fetch fallback
        MetadataResult? fallbackData = null;
        try
        {
            fallbackData = await fallback.FetchMetadataAsync(item);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching from {Provider}", fallback.ProviderName);
        }

        // Merge: primary wins, fallback fills gaps
        var merged = primaryData ?? new MetadataResult();
        if (fallbackData != null)
        {
            if (primaryData == null)
            {
                merged = fallbackData;
            }
            else
            {
                if (string.IsNullOrEmpty(merged.PosterUrl)) merged.PosterUrl = fallbackData.PosterUrl;
                if (!merged.Year.HasValue) merged.Year = fallbackData.Year;
                if (string.IsNullOrEmpty(merged.Album)) merged.Album = fallbackData.Album;
                if (merged.Genres == null || merged.Genres.Count == 0) merged.Genres = fallbackData.Genres;
                if (string.IsNullOrEmpty(merged.Title)) merged.Title = fallbackData.Title;
                if (string.IsNullOrEmpty(merged.Artist)) merged.Artist = fallbackData.Artist;
            }
        }

        if (!string.IsNullOrEmpty(merged.Title) || !string.IsNullOrEmpty(merged.Artist) || merged.Year.HasValue)
        {
            return merged;
        }

        return null;
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

