using SoftMedia.Server.Models;

namespace SoftMedia.Server.Services.Metadata;

public interface IMetadataProvider
{
    LibraryType SupportedType { get; }
    Task<string?> FetchMetadataAsync(string title, string path);
}
