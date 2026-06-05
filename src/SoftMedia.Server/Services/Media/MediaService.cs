using SoftMedia.Server.DTOs;
using SoftMedia.Server.Helpers;
using SoftMedia.Server.Services.Abstractions;

namespace SoftMedia.Server.Services.Media;

public class MediaService : IMediaService
{
    private readonly IMediaRepository _mediaRepository;
    private readonly IStreamSecurityService _securityService;
    private readonly ILogger<MediaService> _logger;

    public MediaService(
        IMediaRepository mediaRepository,
        IStreamSecurityService securityService,
        ILogger<MediaService> logger)
    {
        _mediaRepository = mediaRepository;
        _securityService = securityService;
        _logger = logger;
    }

    public async Task<StreamInfoDto?> GetStreamInfoAsync(Guid mediaId)
    {
        var item = await _mediaRepository.GetByIdWithLibraryAsync(mediaId);
        
        // Return null if item not found or has no library (broken reference)
        if (item == null || item.Library == null)
        {
            return null;
        }

        if (!System.IO.File.Exists(item.Path))
        {
            _logger.LogWarning("File not found on disk: {Path}", item.Path);
            return null;
        }

        // Security: LFI Protection - verify file path is within authorized library directories
        if (!_securityService.IsPathAuthorized(item.Path, item.Library.Paths))
        {
            _logger.LogWarning("LFI attempt blocked: {Path}", item.Path);
            throw new UnauthorizedAccessException("Path not authorized for this library.");
        }

        return new StreamInfoDto
        {
            Path = item.Path,
            ContentType = MimeTypeResolver.GetMimeType(item.Path)
        };
    }
}
