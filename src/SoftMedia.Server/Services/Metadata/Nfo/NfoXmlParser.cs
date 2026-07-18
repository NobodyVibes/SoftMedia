using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using SoftMedia.Server.Models;
using SoftMedia.Server.Services.Abstractions;

namespace SoftMedia.Server.Services.Metadata.Nfo;

/// <summary>
/// Wave D — shared XML parser for Kodi/XBMC <c>.nfo</c> sidecar files. Both
/// <see cref="NfoMovieProvider"/> and <see cref="NfoTvProvider"/> rely on this
/// helper to (1) load NFO XML with XXE-safe settings and (2) project supported
/// elements onto a <see cref="MetadataResult"/>.
///
/// Security:
///   - DTD processing prohibited (blocks XXE).
///   - 1 MiB document cap (blocks billion-laughs / quadratic-blowup).
///   - File opened via <see cref="IFileSystem"/> with <c>FileShare.Read</c>.
///   - Any parse / IO failure logs a warning and returns null. The caller's
///     provider chain falls through to the next provider.
/// </summary>
public static class NfoXmlParser
{
    private const long MaxNfoSizeBytes = 1_000_000;

    /// <summary>
    /// Loads an NFO file with XXE-safe settings. Returns null on:
    ///   - missing file
    ///   - file larger than the 1 MiB cap
    ///   - any IO or XML parse error
    /// </summary>
    public static XDocument? TryLoad(IFileSystem fs, string path, ILogger logger)
    {
        if (!fs.FileExists(path)) return null;

        try
        {
            var size = fs.GetFileLength(path);
            if (size > MaxNfoSizeBytes)
            {
                logger.LogWarning("[NfoXmlParser] Skipping {Path} — exceeds 1 MiB NFO cap ({Size} bytes)", path, size);
                return null;
            }

            using var stream = fs.OpenRead(path);
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                MaxCharactersInDocument = MaxNfoSizeBytes,
                MaxCharactersFromEntities = 1024,
                XmlResolver = null,
            };
            using var reader = XmlReader.Create(stream, settings);
            return XDocument.Load(reader);
        }
        catch (Exception ex) when (ex is XmlException or IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "[NfoXmlParser] Failed to parse {Path}", path);
            return null;
        }
    }

    /// <summary>
    /// Builds a <see cref="MetadataResult"/> from an NFO root element. Both
    /// <c>&lt;movie&gt;</c> and <c>&lt;episodedetails&gt;</c> share most fields,
    /// so the projection logic is unified here.
    ///
    /// Returns null if no usable data was extracted (caller treats as miss).
    /// </summary>
    public static MetadataResult? BuildFromRoot(XElement root)
    {
        var result = new MetadataResult();
        var hasData = false;

        var title = ReadString(root, "title");
        if (title is not null) { result.Title = title; hasData = true; }

        // <plot> preferred over <outline>; both Kodi-recognised.
        var plot = ReadString(root, "plot") ?? ReadString(root, "outline");
        if (plot is not null) { result.Description = plot; hasData = true; }

        // <year> populates Year directly; <premiered> (full ISO date) populates
        // both Year and ReleaseDate when parseable.
        var yearStr = ReadString(root, "year");
        if (yearStr is not null && int.TryParse(yearStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var year))
        {
            result.Year = year;
            hasData = true;
        }

        var premieredStr = ReadString(root, "premiered");
        if (premieredStr is not null &&
            DateTime.TryParse(premieredStr, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var premiered))
        {
            result.ReleaseDate = premiered;
            // Year falls back to the date's year if not explicitly set above.
            if (!result.Year.HasValue) result.Year = premiered.Year;
            hasData = true;
        }

        var contentRating = ReadString(root, "mpaa");
        if (contentRating is not null) { result.ContentRating = contentRating; hasData = true; }

        // ImdbId — prefer <uniqueid type="imdb">, then <imdbid>. Kodi spec says
        // uniqueid is the modern form; older files use the bare element.
        var uniqueImdb = root.Elements("uniqueid")
            .FirstOrDefault(e => string.Equals((string?)e.Attribute("type"), "imdb", StringComparison.OrdinalIgnoreCase));
        var imdbId = (uniqueImdb is not null ? Trim(uniqueImdb.Value) : null) ?? ReadString(root, "imdbid");
        if (imdbId is not null && imdbId.StartsWith("tt", StringComparison.OrdinalIgnoreCase))
        {
            result.ImdbId = imdbId;
            hasData = true;
        }

        var studio = ReadString(root, "studio");
        if (studio is not null) { result.Studio = studio; hasData = true; }

        // Multiple <director> elements possible — first one wins (matches
        // ComicInfoXmlProvider's FirstOfCommaSeparated semantics for comics).
        var director = root.Elements("director")
            .Select(e => Trim(e.Value))
            .FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
        if (director is not null) { result.Director = director; hasData = true; }

        var genres = root.Elements("genre")
            .Select(e => Trim(e.Value))
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Cast<string>()
            .ToList();
        if (genres.Count > 0) { result.Genres = genres; hasData = true; }

        // Rating: prefer <ratings>/<rating>/<value>, fall back to bare <rating>.
        // Kodi 17+ wraps multiple ratings (imdb, tmdb, etc.) inside <ratings>.
        var ratingFromRatings = root.Element("ratings")
            ?.Elements("rating")
            .Select(r => r.Element("value")?.Value)
            .FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
        var ratingStr = ratingFromRatings ?? ReadString(root, "rating");
        if (ratingStr is not null && double.TryParse(ratingStr, NumberStyles.Float, CultureInfo.InvariantCulture, out var rating))
        {
            result.Rating = rating;
            hasData = true;
        }

        // Poster: <thumb> bare or <art><poster>. http(s) URLs flow into PosterUrl as before.
        // R-WI-014: a SAFE relative file name (no root, no drive, no traversal, no separators
        // beyond a single optional subfolder level) is surfaced as LocalPosterFile — the
        // PROVIDER (which knows the NFO's folder) resolves and jails it; the parser stays pure.
        var poster = ReadString(root, "thumb")
            ?? root.Element("art")?.Element("poster")?.Value;
        if (poster is not null)
        {
            poster = Trim(poster);
            if (poster is not null &&
                (poster.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                 || poster.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
            {
                result.PosterUrl = poster;
                hasData = true;
            }
            else if (poster is not null && IsSafeRelativeImagePath(poster))
            {
                result.LocalPosterFile = poster;
                hasData = true;
            }
        }

        // Cast: each <actor> with <name> and optional <role>.
        var cast = root.Elements("actor")
            .Select(a => new CastMember
            {
                Name = Trim(a.Element("name")?.Value) ?? string.Empty,
                Character = Trim(a.Element("role")?.Value),
            })
            .Where(c => !string.IsNullOrWhiteSpace(c.Name))
            .ToList();
        if (cast.Count > 0) { result.Cast = cast; hasData = true; }

        return hasData ? result : null;
    }

    /// <summary>
    /// Reads the text of the first child element with the given local name.
    /// Returns null on missing element, empty value, or whitespace-only value
    /// (matches the "ignore N/A / empty" behaviour throughout MetadataResult).
    /// </summary>
    private static string? ReadString(XElement root, string name)
    {
        var element = root.Element(name);
        if (element is null) return null;
        return Trim(element.Value);
    }

    private static string? Trim(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var trimmed = value.Trim();
        // "N/A" sentinels appear in some legacy NFO files — treat as absent.
        if (string.Equals(trimmed, "N/A", StringComparison.OrdinalIgnoreCase)) return null;
        return trimmed;
    }

    // R-WI-014 — an NFO-supplied local poster reference may only be a simple relative image
    // path (e.g. "poster.jpg" or "extras/poster.png"): never rooted, never a drive/UNC path,
    // never containing traversal segments, and it must carry an image extension. The provider
    // additionally jails the RESOLVED path under the NFO's own folder before any file access.
    private static readonly string[] SafeImageExtensions = { ".jpg", ".jpeg", ".png", ".webp", ".gif" };

    public static bool IsSafeRelativeImagePath(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 256) return false;
        if (Path.IsPathRooted(value)) return false;                       // rejects "/x", "C:\x", "\\unc\x"
        if (value.Contains("..")) return false;                           // traversal
        if (value.Contains(':')) return false;                            // drive/ADS tricks
        if (value.IndexOfAny(Path.GetInvalidPathChars()) >= 0) return false;
        var ext = Path.GetExtension(value);
        return SafeImageExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// R-WI-014 — resolve a parser-approved relative poster reference against the NFO's own
    /// folder and JAIL the result there (belt to the parser's braces: even a value that slipped
    /// the relative-path check cannot escape the folder after canonicalisation). Returns the
    /// absolute path of an existing file, else null.
    /// </summary>
    public static string? ResolveLocalPoster(
        Abstractions.IFileSystem fs, string nfoPath, string? relativePoster, ILogger logger)
    {
        if (string.IsNullOrEmpty(relativePoster)) return null;
        var dir = Path.GetDirectoryName(nfoPath);
        if (string.IsNullOrEmpty(dir)) return null;

        var resolved = Path.GetFullPath(Path.Combine(dir, relativePoster));
        var jail = Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                   + Path.DirectorySeparatorChar;
        if (!resolved.StartsWith(jail, StringComparison.OrdinalIgnoreCase))
        {
            logger.LogWarning("NFO local poster escaped its folder and was rejected: {Value} (nfo: {Nfo})", relativePoster, nfoPath);
            return null;
        }
        return fs.FileExists(resolved) ? resolved : null;
    }
}
