using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SoftMedia.Server.Data;
using SoftMedia.Server.Helpers;
using SoftMedia.Server.Models;
using SoftMedia.Server.Services.Abstractions;
using SoftMedia.Server.Services.Metadata;

namespace SoftMedia.Server.Services.Scanning;

/// <summary>
/// Scanner for book libraries. Flat structure with metadata enrichment from OpenLibrary.
/// </summary>
public class BookScanner : BaseMediaScanner
{
    public override LibraryType SupportedType => LibraryType.Book;
    public override string[] SupportedExtensions => SoftMedia.Server.Constants.MediaExtensions.Book;
    public override string DisplayName => "Book Scanner";

    public BookScanner(
        IServiceScopeFactory scopeFactory,
        ILogger<BookScanner> logger,
        IMediaNotificationService notificationService,
        IMetadataQueue metadataQueue)
        : base(scopeFactory, logger, notificationService, metadataQueue)
    {
    }

    /// <summary>
    /// Process a single document file as a book.
    /// </summary>
    protected override Task<ScanOperationResult> ProcessFileAsync(
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

            // Set Author as part of CAST member via MetadataJson directly? 
            // The OpenLibraryProvider will usually find the author from title search and override it.
            // But we can leave the Title with the parsed string.
            if (!string.IsNullOrEmpty(author) && string.IsNullOrEmpty(book.MetadataJson))
            {
                // We're relying on the OpenLibraryProvider to fetch and properly structure the author as CastMember.
                // It's helpful if the scanner passes the author in the title to the search, or stores it temporarily.
                // OpenLibraryProvider searches "Title" currently. Maybe we can combine them?
                if (!string.IsNullOrEmpty(author))
                {
                    book.Title = $"{author} {title}"; // Help OpenLibrary search by passing Author + Title
                    book.SortTitle = MediaStringHelpers.GetSortTitle(book.Title);
                }
            }

            if (isNew)
            {
                if (book.Id == Guid.Empty) book.Id = Guid.NewGuid();

                context.MediaItems.Add(book);

                _logger.LogDebug("[BookScanner] Added book: {Title}", title);
                return Task.FromResult(new ScanOperationResult(ScanResult.New, book.Id, EnqueueMetadata: true));
            }
            else
            {
                // Check if metadata needs refresh
                var needsEnrichment = string.IsNullOrEmpty(existing!.MetadataJson) ||
                    !existing.MetadataJson.Contains("\"poster\"");

                if (needsEnrichment)
                {
                    _logger.LogDebug("[BookScanner] Queued metadata enrichment: {Title}", title);
                    return Task.FromResult(new ScanOperationResult(ScanResult.Updated, book.Id, EnqueueMetadata: true));
                }

                return Task.FromResult(new ScanOperationResult(ScanResult.Skipped, book.Id, EnqueueMetadata: false));
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[BookScanner] Error processing file: {Path}", filePath);
            return Task.FromResult(new ScanOperationResult(ScanResult.Skipped, Guid.Empty, false));
        }
    }
}
