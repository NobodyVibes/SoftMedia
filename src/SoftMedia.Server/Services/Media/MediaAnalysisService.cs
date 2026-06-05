using Microsoft.Extensions.Logging;
using SoftMedia.Server.Models;
using SoftMedia.Server.Services.Media.Strategies;

namespace SoftMedia.Server.Services.Media;

/// <summary>
/// Delegator service that routes media analysis requests to the appropriate strategy
/// based on the media item's type. This prevents "God Class" anti-pattern by keeping
/// media-type-specific logic encapsulated in individual strategies.
/// </summary>
public class MediaAnalysisService : IMediaAnalysisService
{
    private readonly IEnumerable<IMediaAnalysisStrategy> _strategies;
    private readonly ILogger<MediaAnalysisService> _logger;

    public MediaAnalysisService(
        IEnumerable<IMediaAnalysisStrategy> strategies,
        ILogger<MediaAnalysisService> logger)
    {
        _strategies = strategies;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<bool> AnalyzeAsync(
        MediaItem item,
        string filePath,
        MetadataRefreshMode mode,
        CancellationToken cancellationToken = default)
    {
        if (mode == MetadataRefreshMode.None)
        {
            _logger.LogDebug("[MediaAnalysisService] Skipping analysis for {Title} - Mode is None", item.Title);
            return false;
        }

        var strategy = _strategies.FirstOrDefault(s => s.CanHandle(item.Type));
        if (strategy == null)
        {
            _logger.LogDebug("[MediaAnalysisService] No analysis strategy found for type {Type}", item.Type);
            return false;
        }

        _logger.LogDebug("[MediaAnalysisService] Using {Strategy} for {Title}",
            strategy.GetType().Name, item.Title);

        return await strategy.AnalyzeAsync(item, filePath, mode, cancellationToken);
    }
}
