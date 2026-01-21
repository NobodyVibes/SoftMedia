using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SoftMedia.Server.Data;
using SoftMedia.Server.Services;

namespace SoftMedia.Server.Controllers;

/// <summary>
/// API endpoints for music-related resources (album covers, artist images).
/// </summary>
[ApiController]
[Route("api/v1/music")]
public class MusicController : ControllerBase
{
    private readonly IMusicImageService _imageService;
    private readonly AppDbContext _context;
    private readonly ILogger<MusicController> _logger;

    public MusicController(
        IMusicImageService imageService,
        AppDbContext context,
        ILogger<MusicController> logger)
    {
        _imageService = imageService;
        _context = context;
        _logger = logger;
    }

    /// <summary>
    /// Get album cover art.
    /// </summary>
    /// <param name="albumId">The album ID.</param>
    /// <returns>The cover image or 404.</returns>
    [HttpGet("album/{albumId}/cover")]
    [AllowAnonymous] // Allow anonymous - browser img tags don't send auth headers
    [ResponseCache(Duration = 86400)] // Cache for 24 hours
    public async Task<IActionResult> GetAlbumCover(Guid albumId)
    {
        var result = await _imageService.GetImageBytesAsync(albumId);
        if (result == null)
        {
            _logger.LogDebug("Album cover not found: {AlbumId}", albumId);
            return NotFound();
        }

        return File(result.Value.Data, result.Value.MimeType);
    }

    /// <summary>
    /// Get artist image.
    /// </summary>
    /// <param name="artistId">The artist ID.</param>
    /// <returns>The artist image or 404.</returns>
    [HttpGet("artist/{artistId}/image")]
    [AllowAnonymous] // Allow anonymous - browser img tags don't send auth headers
    [ResponseCache(Duration = 86400)] // Cache for 24 hours
    public async Task<IActionResult> GetArtistImage(Guid artistId)
    {
        var result = await _imageService.GetImageBytesAsync(artistId);
        if (result == null)
        {
            _logger.LogDebug("Artist image not found: {ArtistId}", artistId);
            return NotFound();
        }

        return File(result.Value.Data, result.Value.MimeType);
    }

    /// <summary>
    /// Get track cover art (resolves to album cover).
    /// </summary>
    /// <param name="trackId">The track ID.</param>
    /// <returns>The cover image or 404.</returns>
    [HttpGet("track/{trackId}/cover")]
    [AllowAnonymous] // Allow anonymous - browser img tags don't send auth headers
    [ResponseCache(Duration = 86400)] // Cache for 24 hours
    public async Task<IActionResult> GetTrackCover(Guid trackId)
    {
        var result = await _imageService.GetImageBytesAsync(trackId);
        if (result == null)
        {
            _logger.LogDebug("Track cover not found: {TrackId}", trackId);
            return NotFound();
        }

        return File(result.Value.Data, result.Value.MimeType);
    }
}
