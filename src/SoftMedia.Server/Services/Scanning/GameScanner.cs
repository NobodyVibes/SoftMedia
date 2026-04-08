using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SoftMedia.Server.Data;
using SoftMedia.Server.Helpers;
using SoftMedia.Server.Models;
using SoftMedia.Server.Services.Abstractions;
using SoftMedia.Server.Services.Media;
using SoftMedia.Server.Services.Metadata;

namespace SoftMedia.Server.Services.Scanning;

/// <summary>
/// Scanner for game libraries. Detects ROMs, executables, and common game archives.
/// </summary>
public class GameScanner : BaseMediaScanner
{
    private readonly IMediaAnalysisService _mediaAnalysisService;

    public override LibraryType SupportedType => LibraryType.Game;
    public override string[] SupportedExtensions => SoftMedia.Server.Constants.MediaExtensions.Game;
    public override string DisplayName => "Game Scanner";

    public GameScanner(
        IServiceScopeFactory scopeFactory,
        ILogger<GameScanner> logger,
        IMediaNotificationService notificationService,
        IMediaAnalysisService mediaAnalysisService,
        IMetadataQueue metadataQueue)
        : base(scopeFactory, logger, notificationService, metadataQueue)
    {
        _mediaAnalysisService = mediaAnalysisService;
    }

    /// <summary>
    /// Process a single file as a game.
    /// </summary>

    protected override async Task<ScanOperationResult> ProcessFileAsync(
        AppDbContext context,
        FileDiscoveryResult file,
        MediaItem? existing,
        Library library,
        CancellationToken cancellationToken)
    {
        var filePath = file.Path;
        try
        {
            // Parse title and year from filename using game parser to strip release tags
            var parsed = FileNameParser.ParseGame(filePath);
            var title = parsed.Title;
            var year = parsed.Year;

            if (string.IsNullOrEmpty(title))
                title = Path.GetFileNameWithoutExtension(filePath);

            // Create or update game item
            var isNew = existing == null;
            var game = existing ?? new MediaItem { LibraryId = library.Id };

            game.Title = title;
            game.SortTitle = MediaStringHelpers.GetSortTitle(title);
            game.Path = filePath;
            game.Type = MediaType.Game;
            game.Year = year;
            game.Size = file.Size;
            game.DateModified = file.LastWriteUtc;
            game.Container = Path.GetExtension(filePath).TrimStart('.').ToLowerInvariant();

            if (isNew)
            {
                context.MediaItems.Add(game);
                
                // Delegate local file analysis (ROM headers, file metadata)
                var refreshMode = MetadataRefreshMode.Full;
                await _mediaAnalysisService.AnalyzeAsync(game, filePath, refreshMode, cancellationToken);

                // Enqueue for background enrichment via Base Scanner
                _logger.LogDebug("[GameScanner] Added game: {Title} ({Year})", title, year);
                return new ScanOperationResult(ScanResult.New, game.Id, EnqueueMetadata: true);
            }
            else
            {
                // Check if metadata needs refresh (if no description/overview)
                var needsEnrichment = MetadataEnrichmentPolicy.NeedsEnrichment(game, _strictEnrichment);

                if (needsEnrichment)
                {
                    _logger.LogDebug("[GameScanner] Queued game metadata enrichment: {Title}", title);
                    return new ScanOperationResult(ScanResult.Updated, game.Id, EnqueueMetadata: true);
                }
                else
                {
                    _logger.LogDebug("[GameScanner] Skipped game (metadata complete): {Title}", title);
                    return new ScanOperationResult(ScanResult.Skipped, game.Id, EnqueueMetadata: false);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[GameScanner] Error processing file: {FilePath}", filePath);
            return new ScanOperationResult(ScanResult.Skipped);
        }
    }



// End of class
}
