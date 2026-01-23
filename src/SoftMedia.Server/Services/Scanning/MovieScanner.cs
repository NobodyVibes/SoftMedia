using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SoftMedia.Server.Data;
using SoftMedia.Server.Helpers;
using SoftMedia.Server.Models;
using SoftMedia.Server.Services.Abstractions;
using SoftMedia.Server.Services.Metadata;

namespace SoftMedia.Server.Services.Scanning;

/// <summary>
/// Scanner for movie libraries. Flat structure with metadata enrichment.
/// </summary>
public class MovieScanner : BaseMediaScanner
{
    private readonly IBackgroundImageCacheService _backgroundImageCache;
    private readonly IMediaProbeService _mediaProbeService;

    // Supported video extensions
    private static readonly string[] VideoExtensions =
    {
        "mkv", "mp4", "avi", "m4v", "wmv", "mov", "webm", "ts", "m2ts"
    };

    public override LibraryType SupportedType => LibraryType.Movie;
    public override string[] SupportedExtensions => VideoExtensions;
    public override string DisplayName => "Movie Scanner";

    public MovieScanner(
        IServiceScopeFactory scopeFactory,
        ILogger<MovieScanner> logger,
        IMediaNotificationService notificationService,
        IBackgroundImageCacheService backgroundImageCache,
        IMediaProbeService mediaProbeService)
        : base(scopeFactory, logger, notificationService)
    {
        _backgroundImageCache = backgroundImageCache;
        _mediaProbeService = mediaProbeService;
    }

    /// <summary>
    /// Process a single video file as a movie.
    /// </summary>
    protected override async Task<ScanResult> ProcessFileAsync(
        AppDbContext context,
        string filePath,
        MediaItem? existing,
        Library library,
        CancellationToken cancellationToken)
    {
        try
        {
            // Parse title and year from filename
            var parsed = FileNameParser.ParseMovie(filePath);
            var title = parsed.Title;
            var year = parsed.Year;

            if (string.IsNullOrEmpty(title))
                title = Path.GetFileNameWithoutExtension(filePath);

            // Probe media for technical metadata
            var probe = await _mediaProbeService.ProbeMediaAsync(filePath);
            
            // Create or update movie
            var isNew = existing == null;
            var movie = existing ?? new MediaItem { LibraryId = library.Id };

            movie.Title = title;
            movie.SortTitle = SortableTitle(title);
            movie.Path = filePath;
            movie.Type = MediaType.Movie;
            movie.Year = year;
            movie.Size = new FileInfo(filePath).Length;
            movie.DateModified = File.GetLastWriteTimeUtc(filePath);

            // Populate technical metadata
            if (probe != null)
            {
                movie.Duration = probe.Duration;
                movie.VideoCodec = probe.VideoCodec;
                movie.AudioCodec = probe.AudioCodec;
                movie.Resolution = probe.Resolution;
                movie.Container = Path.GetExtension(filePath).TrimStart('.').ToLowerInvariant();
            }

            if (isNew)
            {
                context.MediaItems.Add(movie);

                // Enrich with OMDb metadata
                await EnrichMovieMetadataAsync(movie, library.Type);

                _logger.LogDebug("[MovieScanner] Added movie: {Title} ({Year})", title, year);
                return ScanResult.New;
            }
            else
            {
                // Put probe data update here too if specific logic needed, but setting it above handles both
                
                // Check if metadata needs refresh
                var needsEnrichment = string.IsNullOrEmpty(existing!.MetadataJson) ||
                    !existing.MetadataJson.Contains("\"poster\"");

                if (needsEnrichment)
                {
                    await EnrichMovieMetadataAsync(movie, library.Type);
                    _logger.LogDebug("[MovieScanner] Enriched movie metadata: {Title}", title);
                    return ScanResult.Updated;
                }
                else
                {
                    _logger.LogDebug("[MovieScanner] Updated movie: {Title}", title);
                    return ScanResult.Updated;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[MovieScanner] Error processing file: {FilePath}", filePath);
            return ScanResult.Skipped;
        }
    }

    /// <summary>
    /// Enrich movie with OMDb metadata.
    /// </summary>
    private async Task EnrichMovieMetadataAsync(MediaItem movie, LibraryType libraryType)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var metadataAggregator = scope.ServiceProvider.GetService<MetadataAggregator>();
            
            if (metadataAggregator != null)
            {
                await metadataAggregator.EnrichMediaItemAsync(movie, libraryType, deferImageCaching: true);
            }

            // Queue for background image caching
            _backgroundImageCache.QueueImageCaching(movie.Id);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[MovieScanner] Failed to enrich movie metadata: {Title}", movie.Title);
        }
    }

    /// <summary>
    /// Create a sortable version of a title.
    /// </summary>
    private static string SortableTitle(string title)
    {
        if (title.StartsWith("The ", StringComparison.OrdinalIgnoreCase))
            return title[4..];
        if (title.StartsWith("A ", StringComparison.OrdinalIgnoreCase))
            return title[2..];
        if (title.StartsWith("An ", StringComparison.OrdinalIgnoreCase))
            return title[3..];
        return title;
    }
}
