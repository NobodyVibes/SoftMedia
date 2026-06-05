using SoftMedia.Server.Models;

namespace SoftMedia.Server.Services.Media.Strategies;

/// <summary>
/// Defines the contract for media-type-specific technical analysis.
/// Each strategy encapsulates the probing/parsing logic for a specific media category.
/// </summary>
public interface IMediaAnalysisStrategy
{
    /// <summary>
    /// Returns true if this strategy can handle the given media type.
    /// </summary>
    bool CanHandle(MediaType mediaType);

    /// <summary>
    /// Analyzes the file at the given path and populates technical metadata on the item.
    /// </summary>
    /// <param name="item">The media item to populate with technical metadata.</param>
    /// <param name="filePath">The absolute path to the media file.</param>
    /// <param name="mode">The refresh mode controlling whether to force re-analysis.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if analysis was performed and item was modified; false if skipped.</returns>
    Task<bool> AnalyzeAsync(MediaItem item, string filePath, MetadataRefreshMode mode, CancellationToken cancellationToken = default);
}
