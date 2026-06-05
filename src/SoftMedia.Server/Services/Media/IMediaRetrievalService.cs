using SoftMedia.Server.Models;

namespace SoftMedia.Server.Services.Media;

public interface IMediaRetrievalService
{
    Task<IEnumerable<MediaItem>> GetRecentMediaAsync(int limit, LibraryType? type);
}
