using SoftMedia.Server.DTOs;

namespace SoftMedia.Server.Services.Abstractions;

public interface IMediaService
{
    /// <summary>
    /// retrieves stream information for a media item, including file path and content type.
    /// Performs security checks (LFI protection) and verification of file existence.
    /// </summary>
    /// <param name="mediaId">The ID of the media item.</param>
    /// <returns>A StreamInfoDto if valid, or null if not found.</returns>
    /// <exception cref="UnauthorizedAccessException">Thrown if the file path is not authorized.</exception>
    Task<StreamInfoDto?> GetStreamInfoAsync(Guid mediaId);
}
