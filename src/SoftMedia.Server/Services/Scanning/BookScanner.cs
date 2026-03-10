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
/// Scanner for book libraries. Flat structure with metadata enrichment from OpenLibrary.
/// </summary>
public class BookScanner : BaseMediaScanner
{
    private readonly IMediaAnalysisService _mediaAnalysisService;

    public override LibraryType SupportedType => LibraryType.Book;
    public override string[] SupportedExtensions => SoftMedia.Server.Constants.MediaExtensions.Book;
    public override string DisplayName => "Book Scanner";

    public BookScanner(
        IServiceScopeFactory scopeFactory,
        ILogger<BookScanner> logger,
        IMediaNotificationService notificationService,
        IMediaAnalysisService mediaAnalysisService,
        IMetadataQueue metadataQueue)
        : base(scopeFactory, logger, notificationService, metadataQueue)
    {
        _mediaAnalysisService = mediaAnalysisService;
    }

    /// <summary>
    /// Process a single document file as a book.
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
            // Parse author and title from filename
            var parsed = FileNameParser.ParseBook(filePath);
            var author = parsed.Author;
            var title = parsed.Title;

            if (string.IsNullOrEmpty(title))
                title = Path.GetFileNameWithoutExtension(filePath);

            // Create or update book
            var isNew = existing == null;
            var book = existing ?? new MediaItem { LibraryId = library.Id };

            book.Title = title;
            book.SortTitle = MediaStringHelpers.GetSortTitle(title);
            book.Path = filePath;
            book.Type = MediaType.Book;
            book.Size = new FileInfo(filePath).Length;
            book.DateModified = File.GetLastWriteTimeUtc(filePath);

            // Store parsed author in MetadataJson for OpenLibraryProvider search enrichment.
            // Do NOT append author to Title — that corrupts the display name.
            if (!string.IsNullOrEmpty(author) && string.IsNullOrEmpty(book.MetadataJson))
            {
                book.MetadataJson = System.Text.Json.JsonSerializer.Serialize(new { author });
            }

            if (isNew)
            {
                if (book.Id == Guid.Empty) book.Id = Guid.NewGuid();

                context.MediaItems.Add(book);

                // Delegate local file analysis (page count, etc.)
                var refreshMode = MetadataRefreshMode.Full;
                await _mediaAnalysisService.AnalyzeAsync(book, filePath, refreshMode, cancellationToken);

                _logger.LogDebug("[BookScanner] Added book: {Title}", title);
                return new ScanOperationResult(ScanResult.New, book.Id, EnqueueMetadata: true);
            }
            else
            {
                // Check if metadata needs refresh
                var needsEnrichment = string.IsNullOrEmpty(existing!.MetadataJson) ||
                    !existing.MetadataJson.Contains("\"poster\"");

                if (needsEnrichment)
                {
                    _logger.LogDebug("[BookScanner] Queued metadata enrichment: {Title}", title);
                    return new ScanOperationResult(ScanResult.Updated, book.Id, EnqueueMetadata: true);
                }

                return new ScanOperationResult(ScanResult.Skipped, book.Id, EnqueueMetadata: false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[BookScanner] Error processing file: {Path}", filePath);
            return new ScanOperationResult(ScanResult.Skipped, Guid.Empty, false);
        }
    }
}
