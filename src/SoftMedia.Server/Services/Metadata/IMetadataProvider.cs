using SoftMedia.Server.Models;

namespace SoftMedia.Server.Services.Metadata;

public interface IMetadataProvider
{
    string ProviderName { get; }
    LibraryType SupportedType { get; }
    Task<string?> FetchMetadataAsync(MediaItem item);
}

/// <summary>
/// Extended interface for metadata providers that require API key management.
/// Allows MetadataRouter to resolve keys without casting to concrete types.
/// </summary>
public interface IKeyedMetadataProvider : IMetadataProvider
{
    /// <summary>
    /// Fetch metadata using the provided API key and key mode.
    /// </summary>
    Task<string?> FetchMetadataWithKeyAsync(MediaItem item, string apiKey, string keyMode);

    /// <summary>
    /// Resolve the active API key based on the configured mode and optional custom key.
    /// Returns null if the provider is disabled or not configured.
    /// </summary>
    string? GetActiveApiKey(string mode, string? customKey);
}
