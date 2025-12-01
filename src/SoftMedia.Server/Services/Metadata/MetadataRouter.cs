using SoftMedia.Server.Models;

namespace SoftMedia.Server.Services.Metadata;

public interface IMetadataRouter
{
    Task<string?> FetchMetadataAsync(string title, string path, LibraryType type);
}

public class MetadataRouter : IMetadataRouter
{
    private readonly IEnumerable<IMetadataProvider> _providers;

    public MetadataRouter(IEnumerable<IMetadataProvider> providers)
    {
        _providers = providers;
    }

    public async Task<string?> FetchMetadataAsync(string title, string path, LibraryType type)
    {
        var provider = _providers.FirstOrDefault(p => p.SupportedType == type);
        if (provider != null)
        {
            return await provider.FetchMetadataAsync(title, path);
        }
        return null;
    }
}
