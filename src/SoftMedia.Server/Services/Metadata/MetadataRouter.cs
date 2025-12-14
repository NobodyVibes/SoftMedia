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

    public MetadataRouter(IEnumerable<IMetadataProvider> providers, ISettingsService settingsService)
    {
        _providers = providers;
        _settingsService = settingsService;
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

        // 2. Find the matching provider
        var provider = _providers.FirstOrDefault(p => p.SupportedType == type && p.ProviderName == preferredProvider)
                       ?? _providers.FirstOrDefault(p => p.SupportedType == type); // Fallback to any provider for type

        if (provider != null)
        {
            return await provider.FetchMetadataAsync(item);
        }
        return null;
    }
}
