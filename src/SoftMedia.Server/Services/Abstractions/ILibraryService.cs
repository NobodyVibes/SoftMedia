using SoftMedia.Server.DTOs;
using SoftMedia.Server.Models;

namespace SoftMedia.Server.Services.Abstractions;

public interface ILibraryService
{
    Task<PagedResult<MediaItemDto>> GetLibraryItemsAsync(Guid libraryId, LibraryItemFilter filter);
    Task<IEnumerable<string>> GetLibraryGenresAsync(Guid libraryId);
    Task<IEnumerable<object>> GetSeriesSeasonsAsync(Guid seriesId);
}
