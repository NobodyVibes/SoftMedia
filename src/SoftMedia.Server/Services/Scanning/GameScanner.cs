using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SoftMedia.Server.Data;
using SoftMedia.Server.Helpers;
using SoftMedia.Server.Models;
using SoftMedia.Server.Services.Abstractions;
using SoftMedia.Server.Services.Metadata;

namespace SoftMedia.Server.Services.Scanning;

/// <summary>
/// Scanner for game libraries. Detects ROMs, executables, and common game archives.
/// </summary>
public class GameScanner : BaseMediaScanner
{
    private readonly IBackgroundImageCacheService _backgroundImageCache;

    // Supported game extensions
    private static readonly string[] GameExtensions =
    {
        // ROMs / Emulation
        "nes", "sfc", "smc", "gba", "gb", "gbc", "n64", "z64", "v64", 
        "nds", "3ds", "iso", "cue", "bin", "ps1", "ps2", "ps3", "psp", 
        "chd", "pkg", "md", "gen", "sms", "gg", "wbu", "wud", "wux", "rpx",
        
        // PC / Executables
        "exe", "lnk", "msi",
        
        // Archives (often used for game packages)
        "zip", "rar", "7z", "tar", "gz"
    };

    public override LibraryType SupportedType => LibraryType.Game;
    public override string[] SupportedExtensions => GameExtensions;
    public override string DisplayName => "Game Scanner";

    public GameScanner(
        IServiceScopeFactory scopeFactory,
        ILogger<GameScanner> logger,
        IMediaNotificationService notificationService,
        IBackgroundImageCacheService backgroundImageCache,
        IMetadataQueue metadataQueue)
        : base(scopeFactory, logger, notificationService, metadataQueue)
    {
        _backgroundImageCache = backgroundImageCache;
    }

    /// <summary>
    /// Process a single file as a game.
    /// </summary>

    protected override async Task<ScanOperationResult> ProcessFileAsync(
        AppDbContext context,
        string filePath,
        MediaItem? existing,
        Library library,
        CancellationToken cancellationToken)
    {
        try
        {
            // Parse title and year from filename using movie parser (works well for Game Name (Year) format)
            var parsed = FileNameParser.ParseMovie(filePath);
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
            game.Size = new FileInfo(filePath).Length;
            game.DateModified = File.GetLastWriteTimeUtc(filePath);
            game.Container = Path.GetExtension(filePath).TrimStart('.').ToLowerInvariant();

            if (isNew)
            {
                context.MediaItems.Add(game);
                
                // Enqueue for background enrichment via Base Scanner
                _logger.LogDebug("[GameScanner] Added game: {Title} ({Year})", title, year);
                return new ScanOperationResult(ScanResult.New, game.Id, EnqueueMetadata: true);
            }
            else
            {
                // Check if metadata needs refresh (if no description/overview)
                var needsEnrichment = string.IsNullOrEmpty(game.Overview);

                if (needsEnrichment)
                {
                    _logger.LogDebug("[GameScanner] Queued game metadata enrichment: {Title}", title);
                    return new ScanOperationResult(ScanResult.Updated, game.Id, EnqueueMetadata: true);
                }
                else
                {
                    _logger.LogDebug("[GameScanner] Updated game: {Title}", title);
                    return new ScanOperationResult(ScanResult.Updated, game.Id, EnqueueMetadata: false);
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
