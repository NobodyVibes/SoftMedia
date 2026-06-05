using Microsoft.Extensions.Logging;
using SoftMedia.Server.Models;
using TagLib;

namespace SoftMedia.Server.Services.Media.Strategies;

/// <summary>
/// Analysis strategy for audio media types (Music tracks).
/// Uses TagLib to extract technical metadata from audio files.
/// </summary>
public class AudioAnalysisStrategy : IMediaAnalysisStrategy
{
    private readonly ILogger<AudioAnalysisStrategy> _logger;

    // Media types handled by this strategy
    private static readonly MediaType[] SupportedTypes =
    {
        MediaType.Audio
    };

    public AudioAnalysisStrategy(ILogger<AudioAnalysisStrategy> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public bool CanHandle(MediaType mediaType) => SupportedTypes.Contains(mediaType);

    /// <inheritdoc />
    public Task<bool> AnalyzeAsync(
        MediaItem item,
        string filePath,
        MetadataRefreshMode mode,
        CancellationToken cancellationToken = default)
    {
        // Smart Probe: Determine if we should actually analyze the file
        if (!ShouldAnalyze(item, filePath, mode))
        {
            _logger.LogDebug("[AudioAnalysisStrategy] Skipping analysis for {Title} - up to date", item.Title);
            return Task.FromResult(false);
        }

        _logger.LogDebug("[AudioAnalysisStrategy] Analyzing {Title} at {Path}", item.Title, filePath);

        try
        {
            using var tagFile = TagLib.File.Create(filePath);
            
            // Map TagLib results to MediaItem
            MapTagsToMediaItem(item, tagFile, filePath);

            _logger.LogDebug("[AudioAnalysisStrategy] Successfully analyzed {Title}: {Duration}s, {Codec}",
                item.Title, item.Duration, item.AudioCodec);

            return Task.FromResult(true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[AudioAnalysisStrategy] Failed to analyze audio file: {Path}", filePath);
            return Task.FromResult(false);
        }
    }

    /// <summary>
    /// Determines if the file should be (re)analyzed based on refresh mode and current state.
    /// </summary>
    private bool ShouldAnalyze(MediaItem item, string filePath, MetadataRefreshMode mode)
    {
        // Full mode always re-analyzes
        if (mode == MetadataRefreshMode.Full)
            return true;

        // Missing mode: only analyze if needed
        if (mode == MetadataRefreshMode.Missing)
        {
            // New item (no existing data)
            if (item.Duration <= 0)
                return true;

            // File has been modified since last analysis
            try
            {
                var fileTime = System.IO.File.GetLastWriteTimeUtc(filePath);
                if (item.DateModified < fileTime)
                    return true;
            }
            catch
            {
                return true;
            }

            return false;
        }

        return false;
    }

    /// <summary>
    /// Maps TagLib properties to MediaItem.
    /// </summary>
    private void MapTagsToMediaItem(MediaItem item, TagLib.File tagFile, string filePath)
    {
        // Duration
        item.Duration = tagFile.Properties.Duration.TotalSeconds;

        // Audio codec
        item.AudioCodec = tagFile.Properties.Codecs
            .FirstOrDefault(c => c is ICodec)?.Description ?? "Unknown";

        // Container format
        item.Container = Path.GetExtension(filePath).TrimStart('.').ToLowerInvariant();

        // File size (handled by scanner, but ensure it's set)
        if (item.Size <= 0)
        {
            item.Size = new FileInfo(filePath).Length;
        }
    }
}
