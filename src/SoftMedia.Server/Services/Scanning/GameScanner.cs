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

            // Create or update game item. SM-WI-055/S10: capture change BEFORE stamping
            // Size/DateModified (same ordering rule as MovieScanner) — the scanner
            // previously had no change detection at all, so a replaced ROM kept its
            // stale analysis until deleted and re-added.
            var isNew = existing == null;
            var fileChanged = existing != null
                && (existing.Size != file.Size || existing.DateModified != file.LastWriteUtc);
            var game = existing ?? new MediaItem { LibraryId = library.Id };

            // SM-WI-010: identity from the filename only for NEW rows; existing rows are
            // matched by Path so the parse cannot have changed — re-stamping could only
            // revert enrichment or admin (Fix-Match/MetadataLocked) edits. Year is
            // fill-only for unlocked existing rows. Mirrors MovieScanner.
            if (isNew)
            {
                game.Title = title;
                game.SortTitle = MediaStringHelpers.GetSortTitle(title);
                game.Year = year;
            }
            else if (!game.MetadataLocked)
            {
                game.Year ??= year;
            }
            game.Path = filePath;
            game.Type = MediaType.Game;
            game.Size = file.Size;
            game.DateModified = file.LastWriteUtc;
            game.Container = Path.GetExtension(filePath).TrimStart('.').ToLowerInvariant();

            if (isNew)
            {
                context.MediaItems.Add(game);

                // Delegate local file analysis (ROM headers, file metadata)
                await _mediaAnalysisService.AnalyzeAsync(game, filePath, MetadataRefreshMode.Full, cancellationToken);

                // Enqueue for background enrichment via Base Scanner
                _logger.LogDebug("[GameScanner] Added game: {Title} ({Year})", title, year);
                return new ScanOperationResult(ScanResult.New, game.Id, EnqueueMetadata: true);
            }
            else
            {
                // SM-WI-055/S10: a replaced file gets a full re-analysis (mirrors
                // MovieScanner's refresh-mode routing).
                if (fileChanged)
                {
                    await _mediaAnalysisService.AnalyzeAsync(game, filePath, MetadataRefreshMode.Full, cancellationToken);
                }

                // Check if metadata needs refresh (if no description/overview)
                var needsEnrichment = MetadataEnrichmentPolicy.NeedsEnrichment(game, _strictEnrichment);

                if (needsEnrichment)
                {
                    _logger.LogDebug("[GameScanner] Queued game metadata enrichment: {Title}", title);
                    return new ScanOperationResult(ScanResult.Updated, game.Id, EnqueueMetadata: true);
                }
                else
                {
                    // Unchanged file + complete metadata = Skipped, so scan counters
                    // reflect reality (mirrors MovieScanner).
                    _logger.LogDebug("[GameScanner] {Action} game (metadata complete): {Title}", fileChanged ? "Updated" : "Skipped", title);
                    return new ScanOperationResult(
                        fileChanged ? ScanResult.Updated : ScanResult.Skipped, game.Id, EnqueueMetadata: false);
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
