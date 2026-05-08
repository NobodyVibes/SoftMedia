using SoftMedia.Server.Models;
using SoftMedia.Server.Services.Abstractions;

namespace SoftMedia.Server.Services.Metadata.Nfo;

/// <summary>
/// Wave D — reads Kodi/XBMC <c>.nfo</c> sidecars for movie files. Network-free
/// by design: behaves like a fallback for users whose libraries already carry
/// curated metadata from Sonarr/Radarr/Kodi setups.
///
/// Discovery order (per Kodi convention):
///   1. <c>&lt;stem&gt;.nfo</c> next to the movie file.
///   2. <c>movie.nfo</c> in the same folder.
/// First hit wins. The result is null when neither exists or parsing fails;
/// the router then falls through to the next provider in the chain.
/// </summary>
public class NfoMovieProvider : IMetadataProvider
{
    private readonly IFileSystem _fs;
    private readonly ILogger<NfoMovieProvider> _logger;

    public LibraryType SupportedType => LibraryType.Movie;
    public string ProviderName => "Nfo";

    public NfoMovieProvider(IFileSystem fs, ILogger<NfoMovieProvider> logger)
    {
        _fs = fs;
        _logger = logger;
    }

    public Task<MetadataResult?> FetchMetadataAsync(MediaItem item)
    {
        // Type guard — this provider only handles the flat Movie type.
        if (item.Type != MediaType.Movie)
            return Task.FromResult<MetadataResult?>(null);

        var nfoPath = ResolveMovieNfoPath(item);
        if (nfoPath is null)
            return Task.FromResult<MetadataResult?>(null);

        var doc = NfoXmlParser.TryLoad(_fs, nfoPath, _logger);
        if (doc?.Root is null)
            return Task.FromResult<MetadataResult?>(null);

        // Root tag must be <movie>; <episodedetails> in a movie folder is a
        // misfile — refuse rather than guess.
        if (!string.Equals(doc.Root.Name.LocalName, "movie", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogInformation(
                "[NfoMovieProvider] {Path} root is <{Tag}>; expected <movie>. Skipping.",
                nfoPath, doc.Root.Name.LocalName);
            return Task.FromResult<MetadataResult?>(null);
        }

        var result = NfoXmlParser.BuildFromRoot(doc.Root);
        // Sufficiency: must at least have a Title, otherwise the router would
        // accept the result and overwrite the existing item title with empty.
        if (result?.Title is null)
            return Task.FromResult<MetadataResult?>(null);

        _logger.LogInformation("[NfoMovieProvider] Loaded NFO for '{Title}' from {Path}", item.Title, nfoPath);
        return Task.FromResult<MetadataResult?>(result);
    }

    private string? ResolveMovieNfoPath(MediaItem item)
    {
        if (string.IsNullOrEmpty(item.Path)) return null;
        var dir = Path.GetDirectoryName(item.Path);
        if (string.IsNullOrEmpty(dir)) return null;

        var stem = Path.GetFileNameWithoutExtension(item.Path);
        var stemNfo = Path.Combine(dir, $"{stem}.nfo");
        if (_fs.FileExists(stemNfo)) return stemNfo;

        var generic = Path.Combine(dir, "movie.nfo");
        if (_fs.FileExists(generic)) return generic;

        return null;
    }
}
