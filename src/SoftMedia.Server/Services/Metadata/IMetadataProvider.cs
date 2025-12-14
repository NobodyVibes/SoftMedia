using SoftMedia.Server.Models;

namespace SoftMedia.Server.Services.Metadata;

public interface IMetadataProvider
{
    string ProviderName { get; }
    LibraryType SupportedType { get; }
    Task<string?> FetchMetadataAsync(MediaItem item);
}
