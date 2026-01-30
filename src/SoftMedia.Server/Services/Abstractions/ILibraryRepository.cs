using SoftMedia.Server.DTOs;
using SoftMedia.Server.Models;

namespace SoftMedia.Server.Services.Abstractions;

public interface ILibraryRepository
{
    /// <summary>
    /// Retrieves a paginated list of media items with their associated user interactions.
    /// </summary>
    Task<PagedResult<(MediaItem Media, UserMediaInteraction? Interaction)>> GetLibraryItemsAsync(Guid libraryId, LibraryItemFilter filter);

    /// <summary>
    /// Retrieves a list of all unique genres in a library from JSON metadata.
    /// </summary>
    Task<IEnumerable<string>> GetLibraryGenresAsync(Guid libraryId);
}
