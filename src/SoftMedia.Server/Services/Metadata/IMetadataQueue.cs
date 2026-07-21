using SoftMedia.Server.Models;

namespace SoftMedia.Server.Services.Metadata;

public interface IMetadataQueue
{
    /// <summary>
    /// Enqueue an item for metadata enrichment. Pass <paramref name="libraryId"/> when the
    /// caller knows it (scanners do) so the item is counted in the per-library pending gauge
    /// that keeps the scan job's Metadata stage honest; retry/refresh paths may omit it.
    /// </summary>
    Task EnqueueMetadataRefreshAsync(Guid mediaId, LibraryType type, bool refreshImages = true, int retryCount = 0, Guid? libraryId = null);

    /// <summary>
    /// Number of items enqueued with a library id that have not finished enrichment yet.
    /// </summary>
    int GetPendingCountForLibrary(Guid libraryId);
}
