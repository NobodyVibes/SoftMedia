using SoftMedia.Server.Models;
using SoftMedia.Server.Services.Abstractions;
using SoftMedia.Server.Services.Media;

namespace SoftMedia.Server.Services.Metadata;

/// <summary>
/// Reads metadata from <c>ComicInfo.xml</c> files embedded in CBZ archives
/// (Anansi spec: https://anansi-project.github.io/docs/comicinfo/intro).
/// Privacy-perfect — no network calls. Mirrors <see cref="EmbeddedMusicProvider"/> in
/// structure and scope.
/// </summary>
public class ComicInfoXmlProvider : IMetadataProvider
{
    private readonly IComicArchiveService _archive;
    private readonly ILogger<ComicInfoXmlProvider> _logger;

    public LibraryType SupportedType => LibraryType.Book;
    public string ProviderName => "ComicInfo";

    public ComicInfoXmlProvider(IComicArchiveService archive, ILogger<ComicInfoXmlProvider> logger)
    {
        _archive = archive;
        _logger = logger;
    }

    public async Task<MetadataResult?> FetchMetadataAsync(MediaItem item)
    {
        // Type guard: this provider only handles comic hierarchy items.
        if (item.Type != MediaType.ComicIssue && item.Type != MediaType.ComicSeries)
        {
            return null;
        }

        var archivePath = ResolveArchivePath(item);
        if (archivePath is null)
        {
            return null;
        }

        try
        {
            var info = await _archive.ExtractComicInfoAsync(archivePath);
            if (info is null)
            {
                _logger.LogInformation("[ComicInfoXmlProvider] No ComicInfo.xml in {Path}", archivePath);
                return null;
            }

            var result = BuildResult(info, item.Type);
            if (result is null)
            {
                _logger.LogInformation("[ComicInfoXmlProvider] ComicInfo.xml present but empty for {Path}", archivePath);
            }
            else
            {
                _logger.LogInformation(
                    "[ComicInfoXmlProvider] Extracted metadata for {ItemTitle} from {Path}: Title={Title}, Description={HasDesc}, Publisher={Publisher}",
                    item.Title, archivePath, result.Title, !string.IsNullOrEmpty(result.Description), result.Publisher);
            }
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[ComicInfoXmlProvider] Failed to extract from {Path}", archivePath);
            return null;
        }
    }

    /// <summary>
    /// For a ComicIssue, the archive is the item's own file. For a ComicSeries, we need
    /// to find a representative issue — pick the lexically-first CBZ in the series folder.
    /// Returns null if no archive can be located.
    /// </summary>
    private string? ResolveArchivePath(MediaItem item)
    {
        if (item.Type == MediaType.ComicIssue)
        {
            return !string.IsNullOrEmpty(item.Path) && File.Exists(item.Path) ? item.Path : null;
        }

        // ComicSeries: Path points at the folder containing issue files.
        if (string.IsNullOrEmpty(item.Path) || !Directory.Exists(item.Path))
        {
            return null;
        }

        try
        {
            var firstCbz = Directory.EnumerateFiles(item.Path, "*.cbz", SearchOption.TopDirectoryOnly)
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
            return firstCbz;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[ComicInfoXmlProvider] Failed to list series folder {Path}", item.Path);
            return null;
        }
    }

    private static MetadataResult? BuildResult(ComicInfoXml info, MediaType itemType)
    {
        var result = new MetadataResult();
        bool hasData = false;

        // Title selection:
        //   ComicIssue → issue <Title>, falling back to "Issue #N" if only the number exists
        //   ComicSeries → <Series>
        if (itemType == MediaType.ComicSeries)
        {
            if (!string.IsNullOrWhiteSpace(info.Series))
            {
                result.Title = info.Series;
                hasData = true;
            }
        }
        else
        {
            if (!string.IsNullOrWhiteSpace(info.Title))
            {
                result.Title = info.Title;
                hasData = true;
            }
            else if (!string.IsNullOrWhiteSpace(info.Number))
            {
                result.Title = $"Issue #{info.Number}";
                hasData = true;
            }
        }

        if (!string.IsNullOrWhiteSpace(info.Summary))
        {
            result.Description = info.Summary;
            hasData = true;
        }

        if (info.Year.HasValue)
        {
            result.Year = info.Year.Value;
            hasData = true;
            if (info.Month is >= 1 and <= 12)
            {
                var day = info.Day is >= 1 and <= 31 ? info.Day.Value : 1;
                try { result.ReleaseDate = new DateTime(info.Year.Value, info.Month.Value, day, 0, 0, 0, DateTimeKind.Utc); }
                catch (ArgumentOutOfRangeException) { /* invalid combo — skip date */ }
            }
        }

        if (!string.IsNullOrWhiteSpace(info.Publisher))
        {
            result.Studio = info.Publisher;
            result.Publisher = info.Publisher;
            hasData = true;
        }

        if (!string.IsNullOrWhiteSpace(info.Writer))
        {
            result.Director = FirstOfCommaSeparated(info.Writer);
            hasData = true;
        }

        if (!string.IsNullOrWhiteSpace(info.Genre))
        {
            result.Genres = SplitCommaSeparated(info.Genre);
            if (result.Genres.Count > 0) hasData = true;
        }

        if (info.PageCount is > 0)
        {
            result.PageCount = info.PageCount;
            hasData = true;
        }

        if (!string.IsNullOrWhiteSpace(info.AgeRating))
        {
            result.ContentRating = info.AgeRating;
            hasData = true;
        }

        return hasData ? result : null;
    }

    private static List<string> SplitCommaSeparated(string raw) =>
        raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    private static string? FirstOfCommaSeparated(string raw)
    {
        var parts = SplitCommaSeparated(raw);
        return parts.Count > 0 ? parts[0] : null;
    }
}
