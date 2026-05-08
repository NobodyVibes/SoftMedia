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
/// Wave E1 — user-owned audio playlists.
///
/// Visibility:
///   - Owner sees their own playlists regardless of <see cref="Playlist.IsPublic"/>.
///   - Non-owners see playlists only if <c>IsPublic == true</c>.
///   - Per-library ACL (Wave C) is applied to playlist *items* on read so a
///     viewer with restricted library access never sees blocked tracks even
///     in someone else's public playlist.
///
/// Mutations are owner-only. Admins do not bypass — playlists are user data,
/// and a user's curated list isn't an admin concern.
/// </summary>
[Authorize]
[ApiController]
[Route("api/v1/[controller]")]
public class PlaylistsController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly IUserLibraryAccessProvider _libraryAccess;
    private readonly ILogger<PlaylistsController> _logger;

    public PlaylistsController(
        AppDbContext db,
        IUserLibraryAccessProvider libraryAccess,
        ILogger<PlaylistsController> logger)
    {
        _db = db;
        _libraryAccess = libraryAccess;
        _logger = logger;
    }

    // ── List / read ──────────────────────────────────────────────────────────

    [HttpGet]
    public async Task<ActionResult<List<PlaylistSummaryDto>>> List()
    {
        var userId = User.GetUserId();

        // Own playlists + everyone else's public ones.
        var rows = await _db.Playlists
            .AsNoTracking()
            .Where(p => p.OwnerUserId == userId || p.IsPublic)
            .Select(p => new
            {
                p.Id,
                p.Name,
                p.Description,
                p.IsPublic,
                p.OwnerUserId,
                OwnerUsername = p.Owner.Username,
                p.CreatedAt,
                p.UpdatedAt,
                ItemCount = p.Items.Count,
            })
            .OrderByDescending(p => p.UpdatedAt)
            .ToListAsync();

        return Ok(rows.Select(p => new PlaylistSummaryDto(
            p.Id, p.Name, p.Description, p.IsPublic,
            p.OwnerUserId == userId, p.OwnerUsername,
            p.ItemCount, p.CreatedAt, p.UpdatedAt)).ToList());
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<PlaylistDetailDto>> Get(Guid id)
    {
        var userId = User.GetUserId();
        var playlist = await _db.Playlists
            .AsNoTracking()
            .Include(p => p.Owner)
            .FirstOrDefaultAsync(p => p.Id == id);

        if (playlist == null) return NotFound();

        // Visibility: owner sees private; others must have IsPublic.
        if (playlist.OwnerUserId != userId && !playlist.IsPublic)
            return NotFound();

        // Wave C — strip items the viewer can't see (their library ACL excludes
        // the source library). The playlist itself stays intact, just trimmed.
        var access = await _libraryAccess.GetCurrentAsync();

        var entries = await _db.PlaylistItems
            .AsNoTracking()
            .Where(pi => pi.PlaylistId == id)
            .Include(pi => pi.MediaItem)
                .ThenInclude(m => m!.Album)
            .Include(pi => pi.MediaItem)
                .ThenInclude(m => m!.MediaItemGenres).ThenInclude(mg => mg.Genre)
            .OrderBy(pi => pi.Order)
            .ToListAsync();

        var visible = access.IsUnrestricted
            ? entries
            : entries.Where(e => access.AllowedLibraryIds.Contains(e.MediaItem.LibraryId)).ToList();

        var items = visible.Select(e => new PlaylistEntryDto(
            e.Id, e.Order,
            MediaItemDto.FromMediaItem(e.MediaItem, "/api/v1/image/proxy")
        )).ToList();

        return Ok(new PlaylistDetailDto(
            playlist.Id,
            playlist.Name,
            playlist.Description,
            playlist.IsPublic,
            playlist.OwnerUserId == userId,
            playlist.Owner.Username,
            playlist.CreatedAt,
            playlist.UpdatedAt,
            items));
    }

    // ── Create / update / delete ─────────────────────────────────────────────

    [HttpPost]
    public async Task<ActionResult<PlaylistSummaryDto>> Create(CreatePlaylistRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
            return BadRequest("Name is required.");
        if (request.Name.Length > 120)
            return BadRequest("Name exceeds 120 character limit.");

        var userId = User.GetUserId();
        var playlist = new Playlist
        {
            OwnerUserId = userId,
            Name = request.Name.Trim(),
            Description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim(),
            IsPublic = request.IsPublic,
        };
        _db.Playlists.Add(playlist);
        await _db.SaveChangesAsync();

        var ownerUsername = await _db.Users.Where(u => u.Id == userId).Select(u => u.Username).FirstAsync();

        return CreatedAtAction(nameof(Get), new { id = playlist.Id },
            new PlaylistSummaryDto(playlist.Id, playlist.Name, playlist.Description,
                playlist.IsPublic, true, ownerUsername, 0, playlist.CreatedAt, playlist.UpdatedAt));
    }

    [HttpPatch("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, UpdatePlaylistRequest request)
    {
        var userId = User.GetUserId();
        var playlist = await _db.Playlists.FirstOrDefaultAsync(p => p.Id == id);
        if (playlist == null) return NotFound();
        if (playlist.OwnerUserId != userId) return NotFound(); // anti-probe per SDD §6.2

        if (request.Name != null)
        {
            var name = request.Name.Trim();
            if (string.IsNullOrEmpty(name)) return BadRequest("Name cannot be empty.");
            if (name.Length > 120) return BadRequest("Name exceeds 120 character limit.");
            playlist.Name = name;
        }
        if (request.Description != null)
        {
            playlist.Description = string.IsNullOrWhiteSpace(request.Description)
                ? null : request.Description.Trim();
        }
        if (request.IsPublic.HasValue)
        {
            playlist.IsPublic = request.IsPublic.Value;
        }

        playlist.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return NoContent();
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id)
    {
        var userId = User.GetUserId();
        var playlist = await _db.Playlists.FirstOrDefaultAsync(p => p.Id == id);
        if (playlist == null) return NotFound();
        if (playlist.OwnerUserId != userId) return NotFound();

        _db.Playlists.Remove(playlist);
        await _db.SaveChangesAsync();
        return NoContent();
    }

    // ── Items: append, remove, reorder ───────────────────────────────────────

    [HttpPost("{id:guid}/items")]
    public async Task<IActionResult> AddItems(Guid id, AddPlaylistItemsRequest request)
    {
        var userId = User.GetUserId();
        var playlist = await _db.Playlists.FirstOrDefaultAsync(p => p.Id == id);
        if (playlist == null) return NotFound();
        if (playlist.OwnerUserId != userId) return NotFound();

        if (request.MediaItemIds == null || request.MediaItemIds.Count == 0)
            return BadRequest("MediaItemIds is required.");

        // v1 scope: audio tracks only. Reject non-audio explicitly so the
        // client gets a clean 400 instead of a silent "added but won't play".
        var requested = request.MediaItemIds.Distinct().ToList();
        var allowed = await _db.MediaItems
            .Where(m => requested.Contains(m.Id) && m.Type == MediaType.Audio)
            .Select(m => m.Id)
            .ToListAsync();

        var rejected = requested.Except(allowed).ToList();
        if (rejected.Count > 0)
            return BadRequest($"Playlist items must be audio tracks. Rejected: {string.Join(", ", rejected)}");

        var nextOrder = await _db.PlaylistItems
            .Where(pi => pi.PlaylistId == id)
            .Select(pi => (int?)pi.Order)
            .MaxAsync() ?? -1;

        // Preserve the request order — duplicates within the request are
        // appended in sequence (a user explicitly putting "Song A, Song A,
        // Song B" gets that exact playback order).
        foreach (var mediaItemId in request.MediaItemIds)
        {
            if (!allowed.Contains(mediaItemId)) continue;
            nextOrder++;
            _db.PlaylistItems.Add(new PlaylistItem
            {
                PlaylistId = id,
                MediaItemId = mediaItemId,
                Order = nextOrder,
            });
        }

        playlist.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return NoContent();
    }

    [HttpDelete("{id:guid}/items/{itemId:guid}")]
    public async Task<IActionResult> RemoveItem(Guid id, Guid itemId)
    {
        var userId = User.GetUserId();
        var playlist = await _db.Playlists.FirstOrDefaultAsync(p => p.Id == id);
        if (playlist == null) return NotFound();
        if (playlist.OwnerUserId != userId) return NotFound();

        var entry = await _db.PlaylistItems.FirstOrDefaultAsync(pi => pi.Id == itemId && pi.PlaylistId == id);
        if (entry == null) return NotFound();

        _db.PlaylistItems.Remove(entry);
        playlist.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        // Compact the Order values so consecutive integers are preserved.
        // Cheap for typical playlists; matters because the reorder endpoint
        // validates by exact Order values.
        var remaining = await _db.PlaylistItems
            .Where(pi => pi.PlaylistId == id)
            .OrderBy(pi => pi.Order)
            .ToListAsync();
        for (int i = 0; i < remaining.Count; i++) remaining[i].Order = i;
        await _db.SaveChangesAsync();

        return NoContent();
    }

    [HttpPut("{id:guid}/order")]
    public async Task<IActionResult> Reorder(Guid id, ReorderPlaylistRequest request)
    {
        var userId = User.GetUserId();
        var playlist = await _db.Playlists.FirstOrDefaultAsync(p => p.Id == id);
        if (playlist == null) return NotFound();
        if (playlist.OwnerUserId != userId) return NotFound();

        var existing = await _db.PlaylistItems
            .Where(pi => pi.PlaylistId == id)
            .ToListAsync();

        // Set-equality validation: the submitted ItemIds must be exactly the
        // current playlist's PlaylistItem.Id set. No additions, no removals,
        // no duplicates. Mismatch = client-server desync; reject with 400 so
        // the client refetches.
        var requested = request.ItemIds ?? new List<Guid>();
        var existingIds = existing.Select(e => e.Id).ToHashSet();
        var requestedSet = requested.ToHashSet();
        if (requested.Count != existing.Count
            || requestedSet.Count != requested.Count
            || !requestedSet.SetEquals(existingIds))
        {
            return BadRequest("ItemIds must be a permutation of the playlist's current items.");
        }

        var indexById = existing.ToDictionary(e => e.Id);
        for (int i = 0; i < requested.Count; i++)
        {
            indexById[requested[i]].Order = i;
        }
        playlist.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return NoContent();
    }
}
