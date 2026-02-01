using SoftMedia.Server.DTOs;
using SoftMedia.Server.Models;

namespace SoftMedia.Server.Services.Abstractions;

public interface ILibraryService
{
    Task<Library?> GetLibraryAsync(Guid id);
    Task<IEnumerable<Library>> GetLibrariesAsync();
    Task<Library> CreateLibraryAsync(CreateLibraryRequest request);
    Task UpdateLibraryAsync(Guid id, UpdateLibraryRequest request);
    Task DeleteLibraryAsync(Guid id);
    Task ReorderLibrariesAsync(List<Guid> orderedIds);

    Task<PagedResult<MediaItemDto>> GetLibraryItemsAsync(Guid libraryId, LibraryItemFilter filter);
    Task<IEnumerable<string>> GetLibraryGenresAsync(Guid libraryId);
    Task<IEnumerable<object>> GetSeriesSeasonsAsync(Guid seriesId);

    Task<IEnumerable<MediaItemDto>> GetSeriesEpisodesAsync(Guid seriesId, Guid userId);
    Task<IEnumerable<MediaItemDto>> GetArtistAlbumsAsync(Guid artistId, Guid userId);
    Task<IEnumerable<MediaItemDto>> GetAlbumTracksAsync(Guid albumId, Guid userId);

    // Scan Jobs Facade
    Task<LibraryScanJob> ScanLibraryAsync(Guid id);
    IEnumerable<LibraryScanJob> GetScanQueue();
    LibraryScanJob? GetScanJobStatus(Guid jobId);
}
