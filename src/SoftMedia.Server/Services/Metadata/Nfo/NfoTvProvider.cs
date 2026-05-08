using SoftMedia.Server.Models;
using SoftMedia.Server.Services.Abstractions;

namespace SoftMedia.Server.Services.Metadata.Nfo;

/// <summary>
/// Wave D — reads Kodi/XBMC <c>.nfo</c> sidecars for TV libraries. Two cases:
///   - <see cref="MediaType.Series"/>: <c>tvshow.nfo</c> inside the series folder.
///     Root must be <c>&lt;tvshow&gt;</c>.
///   - <see cref="MediaType.Episode"/>: <c>&lt;stem&gt;.nfo</c> next to the
///     episode file. Root must be <c>&lt;episodedetails&gt;</c>.
/// Anything else returns null. Season-level NFO is intentionally out of scope
/// for v1 (see plan §"Out of scope").
/// </summary>
public class NfoTvProvider : IMetadataProvider
{
    private readonly IFileSystem _fs;
    private readonly ILogger<NfoTvProvider> _logger;

    public LibraryType SupportedType => LibraryType.TV;
    public string ProviderName => "Nfo";

    public NfoTvProvider(IFileSystem fs, ILogger<NfoTvProvider> logger)
    {
        _fs = fs;
        _logger = logger;
    }

    public Task<MetadataResult?> FetchMetadataAsync(MediaItem item)
    {
        var (nfoPath, expectedRoot) = ResolveNfoForType(item);
        if (nfoPath is null) return Task.FromResult<MetadataResult?>(null);

        var doc = NfoXmlParser.TryLoad(_fs, nfoPath, _logger);
        if (doc?.Root is null) return Task.FromResult<MetadataResult?>(null);

        if (!string.Equals(doc.Root.Name.LocalName, expectedRoot, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogInformation(
                "[NfoTvProvider] {Path} root is <{Tag}>; expected <{Expected}>. Skipping.",
                nfoPath, doc.Root.Name.LocalName, expectedRoot);
            return Task.FromResult<MetadataResult?>(null);
        }

        var result = NfoXmlParser.BuildFromRoot(doc.Root);
        if (result?.Title is null) return Task.FromResult<MetadataResult?>(null);

        _logger.LogInformation("[NfoTvProvider] Loaded NFO for '{Title}' from {Path}", item.Title, nfoPath);
        return Task.FromResult<MetadataResult?>(result);
    }

    /// <summary>
    /// Returns the candidate NFO path and the XML root element name we expect
    /// to find there. Returns (null, null) for unsupported MediaTypes.
    /// </summary>
    private (string? path, string? expectedRoot) ResolveNfoForType(MediaItem item)
    {
        if (string.IsNullOrEmpty(item.Path)) return (null, null);

        switch (item.Type)
        {
            case MediaType.Series:
            {
                // For a Series, item.Path is the series folder.
                if (!_fs.DirectoryExists(item.Path)) return (null, null);
                var nfo = Path.Combine(item.Path, "tvshow.nfo");
                return _fs.FileExists(nfo) ? (nfo, "tvshow") : (null, null);
            }
            case MediaType.Episode:
            {
                var dir = Path.GetDirectoryName(item.Path);
                if (string.IsNullOrEmpty(dir)) return (null, null);
                var stem = Path.GetFileNameWithoutExtension(item.Path);
                var nfo = Path.Combine(dir, $"{stem}.nfo");
                return _fs.FileExists(nfo) ? (nfo, "episodedetails") : (null, null);
            }
            default:
                return (null, null);
        }
    }
}
