using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SkiaSharp;
using SoftMedia.Server.Data;
using SoftMedia.Server.Helpers;
using SoftMedia.Server.Models;
using SoftMedia.Server.Services.Abstractions;
using SoftMedia.Server.Services.Media;
using SoftMedia.Server.Services.Metadata;
using System.Text.Json;

namespace SoftMedia.Server.Services.Scanning;

/// <summary>
/// Scanner for photo libraries. Photos are fully self-describing (EXIF + image header),
/// so unlike the other scanners this one enriches inline at scan time and never enqueues
/// the metadata queue — a 10k-photo library would otherwise flood the shared channel with
/// jobs that only re-read local bytes.
/// </summary>
public class PhotoScanner : BaseMediaScanner
{
    public override LibraryType SupportedType => LibraryType.Photo;
    public override string[] SupportedExtensions => SoftMedia.Server.Constants.MediaExtensions.Photo;
    public override string DisplayName => "Photo Scanner";

    public PhotoScanner(
        IServiceScopeFactory scopeFactory,
        ILogger<PhotoScanner> logger,
        IMediaNotificationService notificationService,
        IMetadataQueue metadataQueue)
        : base(scopeFactory, logger, notificationService, metadataQueue)
    {
    }

    protected override Task<ScanOperationResult> ProcessFileAsync(
        AppDbContext context,
        FileDiscoveryResult file,
        MediaItem? existing,
        Library library,
        CancellationToken cancellationToken)
    {
        var filePath = file.Path;
        try
        {
            // Same unchanged-file contract as MovieScanner: size + mtime match means the
            // bytes didn't change, so the EXIF/dimensions we already extracted still hold.
            // Width == null re-admits items scanned before dimension extraction existed.
            var unchanged = existing != null
                && existing.Size == file.Size
                && existing.DateModified == file.LastWriteUtc
                && existing.Width != null;
            if (unchanged)
            {
                return Task.FromResult(new ScanOperationResult(ScanResult.Skipped, existing!.Id));
            }

            var isNew = existing == null;
            var photo = existing ?? new MediaItem { LibraryId = library.Id };

            var title = Path.GetFileNameWithoutExtension(filePath);
            photo.Title = title;
            photo.SortTitle = MediaStringHelpers.GetSortTitle(title);
            photo.Path = filePath;
            photo.Type = MediaType.Photo;
            photo.Size = file.Size;
            photo.DateModified = file.LastWriteUtc;
            photo.Container = Path.GetExtension(filePath).TrimStart('.').ToLowerInvariant();

            ApplyDimensions(photo, filePath);
            ApplyExif(photo, filePath);

            if (isNew)
            {
                context.MediaItems.Add(photo);
                _logger.LogDebug("[PhotoScanner] Added photo: {Title}", title);
                return Task.FromResult(new ScanOperationResult(ScanResult.New, photo.Id, EnqueueMetadata: false));
            }

            _logger.LogDebug("[PhotoScanner] Updated photo: {Title}", title);
            return Task.FromResult(new ScanOperationResult(ScanResult.Updated, photo.Id, EnqueueMetadata: false));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[PhotoScanner] Error processing file: {FilePath}", filePath);
            return Task.FromResult(new ScanOperationResult(ScanResult.Skipped));
        }
    }

    /// <summary>
    /// Header-only dimension read via SKCodec — no pixel decode, so no decode-bomb risk.
    /// HEIC (no SkiaSharp codec) leaves dimensions null; the item still scans and serves.
    /// </summary>
    private static void ApplyDimensions(MediaItem photo, string filePath)
    {
        using var codec = SKCodec.Create(filePath);
        if (codec == null) return;

        // EXIF orientations 5-8 rotate the display 90°, so the upright image swaps axes.
        var swap = codec.EncodedOrigin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop
            or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
        photo.Width = swap ? codec.Info.Height : codec.Info.Width;
        photo.Height = swap ? codec.Info.Width : codec.Info.Height;
        photo.Resolution = $"{photo.Width}x{photo.Height}";
    }

    private void ApplyExif(MediaItem photo, string filePath)
    {
        var exif = PhotoExifReader.TryRead(filePath);
        if (exif == null)
        {
            _logger.LogDebug("[PhotoScanner] No readable EXIF for {FilePath}", filePath);
            return;
        }

        if (exif.Year.HasValue) photo.Year = exif.Year;
        if (exif.DateTaken.HasValue) photo.ReleaseDate = exif.DateTaken;
        photo.ExifJson = exif.Fields.Count > 0 ? JsonSerializer.Serialize(exif.Fields) : null;

        // Inline enrichment is the whole enrichment for photos — stamp the hash so
        // MetadataEnrichmentPolicy never reports the item as needing a queue pass.
        photo.MetadataHash = "EXIF";
    }
}
