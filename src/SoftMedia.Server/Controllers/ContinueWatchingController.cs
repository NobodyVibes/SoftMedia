using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SoftMedia.Server.DTOs;
using SoftMedia.Server.Extensions;
using SoftMedia.Server.Services.Identity;
using SoftMedia.Server.Services.Media;

namespace SoftMedia.Server.Controllers;

/// <summary>
/// Read-only "Continue Watching" row for the calling user: in-progress Movies and TV shows,
/// newest-first. A TV show appears as a single card that resumes the correct episode (via the
/// shared next-episode resolver); finished movies and fully-watched series are excluded. The list
/// is per-user and honours the per-library ACL + content-rating ceiling.
///
/// Read-only, so API tokens need read:state (B-18) rather than the write:state scope the
/// mutating interaction endpoints require — mirroring WatchlistController.
/// </summary>
[Authorize(Policy = ScopePolicies.ReadState)]
[ApiController]
[Route("api/v1/continue-watching")]
public class ContinueWatchingController : ControllerBase
{
    private readonly IContinueWatchingService _service;

    public ContinueWatchingController(IContinueWatchingService service)
    {
        _service = service;
    }

    [HttpGet]
    public async Task<ActionResult<List<MediaItemDto>>> Get([FromQuery] int limit = 20)
    {
        var userId = User.GetUserId();
        var items = await _service.GetContinueWatchingAsync(userId, limit);
        return Ok(items);
    }
}
