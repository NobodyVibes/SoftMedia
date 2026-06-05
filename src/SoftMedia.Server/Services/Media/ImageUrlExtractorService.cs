using SoftMedia.Server.Models;
using SoftMedia.Server.Services.Abstractions;

namespace SoftMedia.Server.Services.Media;

/// <summary>
/// Extracts image URLs from metadata JSON and enqueues them for background downloading.
/// Removes remote URLs from the metadata object to prevent hotlinking.
/// </summary>
public interface IImageUrlExtractorService
{
    /// <summary>
    /// Extracts image URLs from the metadata result, enqueues each for download,
    /// and strips remote URLs from the result to prevent hotlinking.
    /// </summary>
    /// <param name="item">The media item whose metadata is being processed.</param>
    /// <param name="result">The parsed metadata result (will be modified in-place).</param>
    /// <returns>True if any image URLs were found and enqueued.</returns>
    Task<bool> ExtractAndQueueAsync(MediaItem item, MetadataResult result);
}

/// <inheritdoc />
public class ImageUrlExtractorService : IImageUrlExtractorService
{
    private readonly IImageDownloadQueue _imageDownloadQueue;
    private readonly ILogger<ImageUrlExtractorService> _logger;

    public ImageUrlExtractorService(
        IImageDownloadQueue imageDownloadQueue,
        ILogger<ImageUrlExtractorService> logger)
    {
        _imageDownloadQueue = imageDownloadQueue;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<bool> ExtractAndQueueAsync(MediaItem item, MetadataResult result)
    {
        var downloads = new List<(string Url, int? Season, int? Episode, ImageType Type, int? PersonId)>();

        // 1. Poster / Cover Art / Still
        ExtractPoster(item, result, downloads);

        // 2. Series-specific images (season posters, episode stills)
        if (item.Type == MediaType.Series)
        {
            ExtractSeasonPosters(result, downloads);
            ExtractEpisodeStills(result, downloads);
        }

        // 3. Cast images
        ExtractCastImages(result, downloads);

        if (downloads.Count == 0)
            return false;

        // Enqueue all downloads
        foreach (var download in downloads)
        {
            await _imageDownloadQueue.EnqueueImageDownloadAsync(
                item.Id,
                download.Url,
                download.Season,
                download.Episode,
                item.Type,
                download.Type,
                download.PersonId);
        }

        _logger.LogInformation("Enqueued {Count} image downloads for {Title} ({Type})",
            downloads.Count, item.Title, item.Type);

        return true;
    }

    private void ExtractPoster(MediaItem item, MetadataResult result,
        List<(string Url, int? Season, int? Episode, ImageType Type, int? PersonId)> downloads)
    {
        if (!string.IsNullOrEmpty(result.PosterUrl) && result.PosterUrl.StartsWith("http"))
        {
            _logger.LogInformation("Found poster URL for {Title}: {Url}", item.Title, result.PosterUrl);

            var imageType = item.Type is MediaType.Audio or MediaType.Album
                ? ImageType.AlbumCover
                : ImageType.Poster;

            downloads.Add((result.PosterUrl, null, null, imageType, null));

            // Prevent hotlinking
            result.PosterUrl = null;
        }

        if (!string.IsNullOrEmpty(result.StillUrl) && result.StillUrl.StartsWith("http"))
        {
            downloads.Add((result.StillUrl, null, null, ImageType.Still, null));
            result.StillUrl = null;
        }
    }

    private void ExtractSeasonPosters(MetadataResult result,
        List<(string Url, int? Season, int? Episode, ImageType Type, int? PersonId)> downloads)
    {
        if (result.Seasons == null) return;

        foreach (var season in result.Seasons)
        {
            if (!string.IsNullOrEmpty(season.PosterUrl) && season.PosterUrl.StartsWith("http"))
            {
                downloads.Add((season.PosterUrl, season.Number, null, ImageType.SeasonPoster, null));
                season.PosterUrl = null;
            }
        }
    }

    private void ExtractEpisodeStills(MetadataResult result,
        List<(string Url, int? Season, int? Episode, ImageType Type, int? PersonId)> downloads)
    {
        if (result.Episodes == null) return;

        foreach (var episode in result.Episodes)
        {
            if (!string.IsNullOrEmpty(episode.StillUrl) && episode.StillUrl.StartsWith("http"))
            {
                downloads.Add((episode.StillUrl, episode.SeasonNumber, episode.EpisodeNumber, ImageType.Still, null));
                episode.StillUrl = null;
            }
        }
    }

    private void ExtractCastImages(MetadataResult result,
        List<(string Url, int? Season, int? Episode, ImageType Type, int? PersonId)> downloads)
    {
        if (result.Cast == null) return;

        foreach (var member in result.Cast)
        {
            if (member.Id.HasValue && !string.IsNullOrEmpty(member.ImageUrl) && member.ImageUrl.StartsWith("http"))
            {
                downloads.Add((member.ImageUrl, null, null, ImageType.CastImage, member.Id.Value));
                member.ImageUrl = null;
            }
        }
    }
}
