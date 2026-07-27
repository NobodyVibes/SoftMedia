using SkiaSharp;
using SoftMedia.Server.Helpers;

namespace SoftMedia.Server.Services.Media;

public record PlaylistCoverResult(bool Success, string? RelativePath, string? Error);

public interface IPlaylistCoverService
{
    /// <summary>Stores an uploaded image as this playlist's cover, replacing any previous one.</summary>
    Task<PlaylistCoverResult> SaveAsync(Guid playlistId, Stream upload, CancellationToken ct = default);

    /// <summary>Removes a stored cover; safe to call when there is none.</summary>
    void Delete(Guid playlistId);
}

/// <summary>
/// Custom playlist artwork.
///
/// Everything uploaded is DECODED AND RE-ENCODED to WebP rather than stored as
/// received. That is the security position, not a formatting preference:
///
///   - the bytes that land on disk are produced by our own encoder, so a file
///     that is secretly HTML, SVG-with-script, or a polyglot cannot be served
///     back out of the media cache;
///   - EXIF and every other metadata block is dropped, so uploading a phone
///     photo does not publish its GPS coordinates to anyone the playlist is
///     shared with;
///   - the filename is derived from the playlist id, never from the upload, so
///     there is no path-traversal surface at all.
///
/// Decode is gated by the same pixel budget the thumbnail path uses, so a
/// decompression bomb is refused before it can allocate.
/// </summary>
public class PlaylistCoverService : IPlaylistCoverService
{
    /// <summary>Generous for a cover, small enough that an upload cannot exhaust memory.</summary>
    public const int MaxUploadBytes = 8 * 1024 * 1024;

    /// <summary>Covers render at most a few hundred pixels; anything larger is waste.</summary>
    private const int MaxDimension = 1000;

    private readonly IWebHostEnvironment _env;
    private readonly ILogger<PlaylistCoverService> _logger;
    private readonly string _directory;

    public PlaylistCoverService(IWebHostEnvironment env, ILogger<PlaylistCoverService> logger)
    {
        _env = env;
        _logger = logger;

        var webRoot = !string.IsNullOrEmpty(_env.WebRootPath)
            ? _env.WebRootPath
            : Path.Combine(Environment.CurrentDirectory, "wwwroot");
        _directory = Path.Combine(webRoot, "cache", "images", "playlists");
        Directory.CreateDirectory(_directory);
    }

    public async Task<PlaylistCoverResult> SaveAsync(Guid playlistId, Stream upload, CancellationToken ct = default)
    {
        // Buffer through a bounded MemoryStream: SkiaSharp needs random access, and
        // the cap stops a client streaming an unbounded body into memory even if a
        // request-size limit is somehow not applied.
        using var buffer = new MemoryStream();
        var copied = await CopyBoundedAsync(upload, buffer, MaxUploadBytes + 1, ct);
        if (copied > MaxUploadBytes)
            return new PlaylistCoverResult(false, null, "That image is too large (8 MB maximum).");
        if (copied == 0)
            return new PlaylistCoverResult(false, null, "The uploaded file was empty.");

        buffer.Position = 0;

        // Probe before decoding. A file whose bytes are not a decodable image gets
        // rejected here — the extension and Content-Type the client sent are never
        // consulted, because neither is evidence of anything.
        using (var codec = SKCodec.Create(new SKManagedStream(buffer, false)))
        {
            if (codec == null)
                return new PlaylistCoverResult(false, null, "That file is not an image we can read.");
            if (!ImageSafety.IsWithinBudget(codec.Info.Width, codec.Info.Height))
            {
                _logger.LogWarning("Refusing oversized playlist cover for {PlaylistId}", playlistId);
                return new PlaylistCoverResult(false, null, "That image's dimensions are too large.");
            }
        }

        buffer.Position = 0;
        using var decoded = SKBitmap.Decode(buffer);
        if (decoded == null)
            return new PlaylistCoverResult(false, null, "That image could not be decoded.");

        using var square = CropToSquare(decoded);
        if (square == null)
            return new PlaylistCoverResult(false, null, "That image could not be processed.");

        using var image = SKImage.FromBitmap(square);
        using var data = image.Encode(SKEncodedImageFormat.Webp, 85);

        var fileName = $"{playlistId}.webp";
        var fullPath = Path.Combine(_directory, fileName);

        // Write then move, so a failed encode cannot leave a truncated cover in
        // place of a good one.
        var tempPath = fullPath + ".tmp";
        await using (var file = File.Create(tempPath))
        {
            data.SaveTo(file);
        }
        File.Move(tempPath, fullPath, overwrite: true);

        // Cache-busting suffix: the filename is derived from the playlist id and so
        // never changes, and browsers would keep showing the previous cover.
        var stamp = DateTime.UtcNow.Ticks;
        return new PlaylistCoverResult(true, $"/cache/images/playlists/{fileName}?v={stamp}", null);
    }

    public void Delete(Guid playlistId)
    {
        var path = Path.Combine(_directory, $"{playlistId}.webp");
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException e)
        {
            // A cover we cannot unlink is cosmetic debris, not a failed operation —
            // the row's pointer is cleared regardless.
            _logger.LogWarning(e, "Could not delete playlist cover {Path}", path);
        }
    }

    /// <summary>
    /// Centre-crops to a square. Playlist art renders in square tiles everywhere
    /// (cards, the detail rail, the mosaic it replaces), so cropping once here
    /// beats every consumer having to letterbox.
    /// </summary>
    private static SKBitmap? CropToSquare(SKBitmap source)
    {
        var side = Math.Min(source.Width, source.Height);
        var target = Math.Min(side, MaxDimension);

        var left = (source.Width - side) / 2;
        var top = (source.Height - side) / 2;

        using var cropped = new SKBitmap(side, side);
        if (!source.ExtractSubset(cropped, new SKRectI(left, top, left + side, top + side)))
            return null;

        return cropped.Resize(new SKImageInfo(target, target), SKSamplingOptions.Default);
    }

    /// <summary>Copies at most <paramref name="limit"/> bytes; returns how many were read.</summary>
    private static async Task<long> CopyBoundedAsync(Stream source, Stream destination, long limit, CancellationToken ct)
    {
        var chunk = new byte[81920];
        long total = 0;
        int read;
        while ((read = await source.ReadAsync(chunk, ct)) > 0)
        {
            total += read;
            if (total > limit) return total;
            await destination.WriteAsync(chunk.AsMemory(0, read), ct);
        }
        return total;
    }
}
