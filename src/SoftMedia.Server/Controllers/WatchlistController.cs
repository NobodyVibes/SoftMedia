using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SoftMedia.Server.Data;
using SoftMedia.Server.DTOs;
using SoftMedia.Server.Extensions;
using SoftMedia.Server.Models;
using SoftMedia.Server.Services.Security.LibraryAccess;

namespace SoftMedia.Server.Controllers;

/// <summary>
/// Wave E3 — read-only listing of the calling user's watchlist. Mutations
/// (add/remove) live on InteractionController for symmetry with favorite,
/// rating, etc.
///
/// Per-library ACL (Wave C) is applied so an item the user has watchlisted
/// in the past but no longer has access to is silently stripped. The user's
/// `UserMediaInteraction` row is left in place — re-granting access restores
/// the item to their watchlist without the user having to re-add it.
/// </summary>
[Authorize]
[ApiController]
[Route("api/v1/watchlist")]
public class WatchlistController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly IUserLibraryAccessProvider _libraryAccess;

    public WatchlistController(AppDbContext db, IUserLibraryAccessProvider libraryAccess)
    {
        _db = db;
        _libraryAccess = libraryAccess;
    }

    [HttpGet]
    public async Task<ActionResult<List<MediaItemDto>>> Get([FromQuery] int limit = 50)
    {
        var userId = User.GetUserId();
        var access = await _libraryAccess.GetCurrentAsync();

        // Cap the limit so a malicious caller can't request 100k rows and OOM us.
        limit = Math.Clamp(limit, 1, 200);

        // Build the query: watchlisted interactions for this user, ordered by
        // when they were added (newest first). The limit is applied AFTER the
        // ACL filter to keep the visible-result count consistent — otherwise
        // a user with restricted access would silently see a smaller list
        // even when more eligible items exist past the cap.
        IQueryable<Models.UserMediaInteraction> query = _db.UserMediaInteractions
            .AsNoTracking()
            .Where(i => i.UserId == userId && i.IsWatchlisted)
            .OrderByDescending(i => i.WatchlistedAt);

        // Materialise just the media-item ids first so we don't pay for
        // hydrating MediaItem rows we'll filter out.
        var candidates = await query
            .Select(i => new { i.MediaItemId, i.WatchlistedAt })
            .ToListAsync();

        if (candidates.Count == 0)
        {
            return Ok(new List<MediaItemDto>());
        }

        var ids = candidates.Select(c => c.MediaItemId).ToList();

        // Music items can no longer be added to the watchlist (playlists cover
        // that concept), but rows from before the toggle endpoint started
        // rejecting them may still exist — strip on read so legacy data
        // doesn't leak into the UI.
        var items = await _db.MediaItems
            .AsNoTracking()
            .Include(m => m.MediaItemGenres).ThenInclude(mg => mg.Genre)
            .Where(m => ids.Contains(m.Id)
                && m.Type != MediaType.Audio
                && m.Type != MediaType.Album
                && m.Type != MediaType.Artist)
            .ApplyLibraryAccessFilter(access)
            .ToListAsync();

        // Reorder by the watchlist-stamp ordering and trim to limit.
        var byWatchedAt = candidates
            .Where(c => items.Any(m => m.Id == c.MediaItemId))
            .Take(limit)
            .ToList();

        var byId = items.ToDictionary(m => m.Id);
        var dtos = byWatchedAt
            .Select(c => byId[c.MediaItemId])
            .Select(m => MediaItemDto.FromMediaItem(m, "/api/v1/image/proxy"))
            .ToList();

        return Ok(dtos);
    }
}
