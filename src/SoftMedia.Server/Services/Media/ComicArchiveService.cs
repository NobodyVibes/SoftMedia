using System.IO.Compression;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Microsoft.Extensions.Caching.Memory;
using SharpCompress.Archives.Rar;
using SoftMedia.Server.Services.Abstractions;

namespace SoftMedia.Server.Services.Media;

/// <summary>
/// Comic archive reader covering CBZ (ZIP) and CBR (RAR). Entries are filtered
/// to known image types and emitted in natural order so page2.jpg precedes
/// page10.jpg. Extracted pages are cached in memory to avoid re-unzipping on
/// every page turn. Encrypted or malformed archives surface as exceptions;
/// controllers catch and translate those.
/// </summary>
public class ComicArchiveService : IComicArchiveService
{
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".webp", ".gif"
    };

    // Audit L4: decompression-bomb guards. A single comic page is an image — 64 MB is already
    // generous — and ComicInfo.xml is tiny metadata. Caps are on the DECOMPRESSED size, so a
    // small-compressed entry that inflates to GB is rejected before it can exhaust memory.
    private const long MaxPageBytes = 64L * 1024 * 1024;
    private const long MaxComicInfoChars = 1_000_000; // ~1 MiB of XML text

    // Page cache: entries expire after 10 min of disuse, capped at ~50MB total size.
    private static readonly MemoryCacheEntryOptions PageCacheOptions = new()
    {
        SlidingExpiration = TimeSpan.FromMinutes(10),
        Size = 1
    };

    private readonly IMemoryCache _cache;
    private readonly ILogger<ComicArchiveService> _logger;

    public ComicArchiveService(IMemoryCache cache, ILogger<ComicArchiveService> logger)
    {
        _cache = cache;
        _logger = logger;
    }

    public bool IsSupportedArchive(string filePath)
    {
        var ext = Path.GetExtension(filePath);
        return string.Equals(ext, ".cbz", StringComparison.OrdinalIgnoreCase)
            || string.Equals(ext, ".cbr", StringComparison.OrdinalIgnoreCase);
    }

    public Task<int> GetPageCountAsync(string filePath, CancellationToken cancellationToken = default)
    {
        EnsureSupported(filePath);
        var cacheKey = $"comic:count:{filePath}:{File.GetLastWriteTimeUtc(filePath).Ticks}";
        var count = _cache.GetOrCreate(cacheKey, entry =>
        {
            entry.SlidingExpiration = TimeSpan.FromMinutes(30);
            entry.Size = 1;
            using var archive = OpenArchive(filePath);
            return GetOrderedImageEntries(archive).Count;
        });
        return Task.FromResult(count);
    }

    public Task<ComicPage?> GetPageAsync(string filePath, int pageNumber, CancellationToken cancellationToken = default)
    {
        EnsureSupported(filePath);
        if (pageNumber < 1)
        {
            return Task.FromResult<ComicPage?>(null);
        }

        var lastWrite = File.GetLastWriteTimeUtc(filePath).Ticks;
        var cacheKey = $"comic:page:{filePath}:{lastWrite}:{pageNumber}";

        if (_cache.TryGetValue(cacheKey, out ComicPage? cached) && cached is not null)
        {
            return Task.FromResult<ComicPage?>(cached);
        }

        using var archive = OpenArchive(filePath);
        var entries = GetOrderedImageEntries(archive);
        if (pageNumber > entries.Count)
        {
            return Task.FromResult<ComicPage?>(null);
        }

        var entry = entries[pageNumber - 1];
        using var stream = entry.OpenStream();
        var data = ReadAllBounded(stream, MaxPageBytes, entry.FullName);
        var page = new ComicPage(data, ResolveContentType(entry.FullName));

        _cache.Set(cacheKey, page, PageCacheOptions);
        return Task.FromResult<ComicPage?>(page);
    }

    public Task<ComicInfoXml?> ExtractComicInfoAsync(string filePath, CancellationToken cancellationToken = default)
    {
        EnsureSupported(filePath);

        if (!File.Exists(filePath)) return Task.FromResult<ComicInfoXml?>(null);
        var lastWrite = File.GetLastWriteTimeUtc(filePath).Ticks;
        var cacheKey = $"comic:info:{filePath}:{lastWrite}";

        if (_cache.TryGetValue(cacheKey, out ComicInfoXml? cached))
        {
            return Task.FromResult(cached);
        }

        ComicInfoXml? parsed = null;
        try
        {
            using var archive = OpenArchive(filePath);
            // Case-insensitive match against the root ComicInfo.xml. Some rippers produce
            // lowercase or nested paths — we only honour the root per Anansi spec but
            // tolerate any casing of the filename.
            var entry = archive.Entries.FirstOrDefault(e =>
                string.Equals(e.FullName, "ComicInfo.xml", StringComparison.OrdinalIgnoreCase));

            if (entry is not null)
            {
                using var stream = entry.OpenStream();
                // Audit L4: cap the decompressed XML size and disable DTDs/external entities
                // (the latter is the XmlReader default, but we make it explicit).
                var settings = new XmlReaderSettings
                {
                    DtdProcessing = DtdProcessing.Prohibit,
                    XmlResolver = null,
                    MaxCharactersInDocument = MaxComicInfoChars,
                };
                using var reader = XmlReader.Create(stream, settings);
                var doc = XDocument.Load(reader);
                parsed = MapComicInfo(doc);
            }
        }
        catch (Exception ex)
        {
            // Malformed / encrypted archive or malformed XML — treat as absent per the
            // spec's optional-field policy. Page read paths still surface the exception.
            _logger.LogWarning(ex, "Failed to parse ComicInfo.xml from {Path}", filePath);
            parsed = null;
        }

        _cache.Set(cacheKey, parsed, new MemoryCacheEntryOptions
        {
            SlidingExpiration = TimeSpan.FromMinutes(30),
            Size = 1
        });
        return Task.FromResult(parsed);
    }

    private static ComicInfoXml? MapComicInfo(XDocument doc)
    {
        var root = doc.Root;
        if (root is null || !string.Equals(root.Name.LocalName, "ComicInfo", StringComparison.OrdinalIgnoreCase))
            return null;

        string? S(string name) => root.Elements().FirstOrDefault(
            e => string.Equals(e.Name.LocalName, name, StringComparison.OrdinalIgnoreCase))?.Value?.Trim() is { Length: > 0 } v ? v : null;

        int? I(string name)
        {
            var raw = S(name);
            return int.TryParse(raw, out var n) ? n : (int?)null;
        }

        return new ComicInfoXml
        {
            Title = S("Title"),
            Series = S("Series"),
            Number = S("Number"),
            Count = I("Count"),
            Volume = I("Volume"),
            AlternateSeries = S("AlternateSeries"),
            AlternateNumber = S("AlternateNumber"),
            Year = I("Year"),
            Month = I("Month"),
            Day = I("Day"),
            Summary = S("Summary"),
            Notes = S("Notes"),
            Genre = S("Genre"),
            Tags = S("Tags"),
            Web = S("Web"),
            LanguageISO = S("LanguageISO"),
            Writer = S("Writer"),
            Penciller = S("Penciller"),
            Inker = S("Inker"),
            Colorist = S("Colorist"),
            Letterer = S("Letterer"),
            CoverArtist = S("CoverArtist"),
            Editor = S("Editor"),
            Translator = S("Translator"),
            Publisher = S("Publisher"),
            Imprint = S("Imprint"),
            PageCount = I("PageCount"),
            Format = S("Format"),
            AgeRating = S("AgeRating")
        };
    }

    private void EnsureSupported(string filePath)
    {
        if (!IsSupportedArchive(filePath))
        {
            _logger.LogWarning("Unsupported comic archive format: {Path}", filePath);
            throw new NotSupportedException($"Unsupported archive format: {Path.GetExtension(filePath)}");
        }
    }

    private static IComicArchiveReader OpenArchive(string filePath)
    {
        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        return ext switch
        {
            ".cbz" => ZipComicArchiveReader.Open(filePath),
            ".cbr" => RarComicArchiveReader.Open(filePath),
            _ => throw new NotSupportedException($"Unsupported archive format: {ext}")
        };
    }

    private static List<ComicArchiveEntry> GetOrderedImageEntries(IComicArchiveReader archive)
    {
        return archive.Entries
            .Where(e => !string.IsNullOrEmpty(e.FullName)
                        && ImageExtensions.Contains(Path.GetExtension(e.FullName)))
            .OrderBy(e => e.FullName, NaturalStringComparer.Instance)
            .ToList();
    }

    /// <summary>
    /// Copies a (decompressed) entry stream into memory but aborts if it exceeds
    /// <paramref name="maxBytes"/> — defeating a zip/rar bomb whose tiny compressed entry
    /// inflates to gigabytes (audit L4). The cap is enforced on bytes actually read, so a
    /// lying archive header can't bypass it.
    /// </summary>
    private static byte[] ReadAllBounded(Stream stream, long maxBytes, string entryName)
    {
        using var ms = new MemoryStream();
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            total += read;
            if (total > maxBytes)
                throw new InvalidOperationException($"Comic entry '{entryName}' exceeds the {maxBytes}-byte limit.");
            ms.Write(buffer, 0, read);
        }
        return ms.ToArray();
    }

    private static string ResolveContentType(string entryName)
    {
        var ext = Path.GetExtension(entryName).ToLowerInvariant();
        return ext switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".webp" => "image/webp",
            ".gif" => "image/gif",
            _ => "application/octet-stream"
        };
    }

    // ──────────────────────────────────────────────────────────── Archive adapters

    /// <summary>
    /// Thin disposable wrapper around the underlying archive so page-count,
    /// page-extract, and ComicInfo extraction share one enumeration model
    /// regardless of whether the file is ZIP- or RAR-backed.
    /// </summary>
    private interface IComicArchiveReader : IDisposable
    {
        IEnumerable<ComicArchiveEntry> Entries { get; }
    }

    /// <summary>
    /// Lightweight archive-entry view. <see cref="OpenStream"/> must return a
    /// freshly-opened stream each call so callers can read entries repeatedly
    /// within one archive session. Reference type so <c>FirstOrDefault</c>
    /// can return null for absent entries (e.g. ComicInfo.xml).
    /// </summary>
    private sealed record ComicArchiveEntry(string FullName, Func<Stream> OpenStream);

    private sealed class ZipComicArchiveReader : IComicArchiveReader
    {
        private readonly ZipArchive _archive;

        private ZipComicArchiveReader(ZipArchive archive) { _archive = archive; }

        public static ZipComicArchiveReader Open(string filePath) =>
            new(ZipFile.OpenRead(filePath));

        public IEnumerable<ComicArchiveEntry> Entries =>
            _archive.Entries.Select(e => new ComicArchiveEntry(e.FullName, e.Open));

        public void Dispose() => _archive.Dispose();
    }

    private sealed class RarComicArchiveReader : IComicArchiveReader
    {
        private readonly RarArchive _archive;

        private RarComicArchiveReader(RarArchive archive) { _archive = archive; }

        public static RarComicArchiveReader Open(string filePath) =>
            new(RarArchive.Open(filePath));

        public IEnumerable<ComicArchiveEntry> Entries =>
            _archive.Entries
                .Where(e => !e.IsDirectory)
                // SharpCompress surfaces the entry path as Key; fall back to empty
                // so downstream filename filters stay defensive.
                .Select(e => new ComicArchiveEntry(
                    e.Key ?? string.Empty,
                    () => e.OpenEntryStream()));

        public void Dispose() => _archive.Dispose();
    }

    /// <summary>
    /// Compares strings with numeric runs treated as numbers (so "page2" &lt; "page10").
    /// </summary>
    private sealed class NaturalStringComparer : IComparer<string>
    {
        public static readonly NaturalStringComparer Instance = new();

        private static readonly Regex TokenPattern = new(@"(\d+)|(\D+)", RegexOptions.Compiled);

        public int Compare(string? x, string? y)
        {
            if (x is null && y is null) return 0;
            if (x is null) return -1;
            if (y is null) return 1;

            var xTokens = TokenPattern.Matches(x);
            var yTokens = TokenPattern.Matches(y);
            var limit = Math.Min(xTokens.Count, yTokens.Count);

            for (int i = 0; i < limit; i++)
            {
                var xt = xTokens[i].Value;
                var yt = yTokens[i].Value;
                bool xIsNum = char.IsDigit(xt[0]);
                bool yIsNum = char.IsDigit(yt[0]);

                int cmp;
                if (xIsNum && yIsNum)
                {
                    cmp = long.Parse(xt).CompareTo(long.Parse(yt));
                }
                else
                {
                    cmp = string.Compare(xt, yt, StringComparison.OrdinalIgnoreCase);
                }

                if (cmp != 0) return cmp;
            }

            return xTokens.Count.CompareTo(yTokens.Count);
        }
    }
}
