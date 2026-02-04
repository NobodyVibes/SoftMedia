using SoftMedia.Server.Models;

namespace SoftMedia.Server.Services.Metadata;

public interface IMetadataQueue
{
    Task EnqueueMetadataRefreshAsync(Guid mediaId, LibraryType type);
}
