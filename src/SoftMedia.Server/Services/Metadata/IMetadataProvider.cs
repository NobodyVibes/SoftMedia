using SoftMedia.Server.Models;

namespace SoftMedia.Server.Services.Metadata;

public interface IMetadataProvider
{
    LibraryType SupportedType { get; }
    string ProviderName { get; }
    Task<MetadataResult?> FetchMetadataAsync(MediaItem item);
}

/// <summary>
/// A single candidate result from a manual provider search (P3-WI-003 "Fix Match").
/// <see cref="ProviderItemId"/> is the provider-native id (Wikidata Q-id, TVMaze id,
/// IMDb id, MBID, OL key) — handed back to the provider to fetch full metadata when
/// the admin picks this candidate.
/// </summary>
public record MetadataSearchCandidate(
    string ProviderName,
    string ProviderItemId,
    string Title,
    int? Year,
    string? PosterUrl,
    string? Subtitle);

/// <summary>
/// Optional capability: a provider that can search by free-text query and return
/// ranked candidates. Used by the admin "Fix Match" flow (P3-WI-003). Providers that
/// implement this become reachable from POST /api/v1/admin/match/{id}/search.
/// </summary>
public interface ISearchableMetadataProvider : IMetadataProvider
{
    Task<IReadOnlyList<MetadataSearchCandidate>> SearchAsync(string query, int? year, CancellationToken ct);

    /// <summary>
    /// Fetches the full metadata for a previously-returned candidate. Equivalent to
    /// FetchMetadataAsync but keyed on the candidate's ProviderItemId rather than the
    /// MediaItem's filename, so the admin can override a wrong match without renaming files.
    /// </summary>
    Task<MetadataResult?> FetchByCandidateAsync(string providerItemId, CancellationToken ct);
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
    Task<MetadataResult?> FetchMetadataWithKeyAsync(MediaItem item, string apiKey, string keyMode);

    /// <summary>
    /// Resolve the active API key based on the configured mode and optional custom key.
    /// Returns null if the provider is disabled or not configured.
    /// </summary>
    string? GetActiveApiKey(string mode, string? customKey);
}
