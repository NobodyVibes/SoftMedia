using SoftMedia.Server.Models;

namespace SoftMedia.Server.Services.Media;

/// <summary>
/// Service for performing technical analysis on media files.
/// Acts as a delegator that routes analysis requests to the appropriate strategy
/// based on media type.
/// </summary>
public interface IMediaAnalysisService
{
    /// <summary>
    /// Analyzes the media file and populates technical metadata on the item.
    /// Delegates to the appropriate strategy based on the item's media type.
    /// </summary>
    /// <param name="item">The media item to analyze.</param>
    /// <param name="filePath">The absolute path to the media file.</param>
    /// <param name="mode">The refresh mode controlling whether to force re-analysis.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if analysis was performed; false if skipped or no strategy available.</returns>
    Task<bool> AnalyzeAsync(MediaItem item, string filePath, MetadataRefreshMode mode, CancellationToken cancellationToken = default);
}
