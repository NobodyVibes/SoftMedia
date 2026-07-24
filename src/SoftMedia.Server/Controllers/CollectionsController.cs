using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SoftMedia.Server.Data;
using SoftMedia.Server.DTOs;
using SoftMedia.Server.Models;
using SoftMedia.Server.Services.Identity;
using SoftMedia.Server.Services.Media;
using SoftMedia.Server.Services.Security.ContentRating;
using SoftMedia.Server.Services.Security.LibraryAccess;

namespace SoftMedia.Server.Controllers;

/// <summary>
/// Wave E2 — movie collections (franchises). Read endpoints are open to all
/// authenticated users with per-library ACL applied; mutation endpoints are
/// admin-only and only operate on manual collections (those with no
/// WikidataId). Auto-collections are read-only — if a user wants a different
/// grouping they create a manual collection alongside.
///
/// View behaviour (per maintainer reminder):
///   - Library view is unchanged: each movie still appears as its own card.
///     Collections are an aggregation, not a replacement.
///   - Movie detail view shows "More from this collection" via the
///     by-movie endpoint.
/// </summary>
[Authorize(Policy = ScopePolicies.ReadLibrary)] // B-18: collection metadata = catalog data
[ApiController]
[Route("api/v1/[controller]")]
public class CollectionsController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly IUserLibraryAccessProvider _libraryAccess;
    private readonly IUserContentRatingProvider _ratings;
    private readonly ILogger<CollectionsController> _logger;

    public CollectionsController(
        AppDbContext db,
        IUserLibraryAccessProvider libraryAccess,
        IUserContentRatingProvider ratings,
        ILogger<CollectionsController> logger)
    {
        _db = db;
        _libraryAccess = libraryAccess;
        _ratings = ratings;
        _logger = logger;
    }

    // ── List / read ──────────────────────────────────────────────────────────

    /// <summary>
    /// Returns collections that have ≥2 movies the caller can see (research
    /// finding: showing a 1-movie "collection" is noise).
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<List<CollectionSummaryDto>>> List()
    {
        var access = await _libraryAccess.GetCurrentAsync();
        // Audit wave-2 M-1: the visible-count must honour BOTH the per-library ACL and the
        // content-rating ceiling, otherwise a rating-restricted caller sees over-rating movies
        // (and a hidden movie still counts toward the >=2 display threshold).
        var ceilings = await _ratings.GetCurrentAsync();

        // Pull every collection's items once, count visible-to-caller per id,
        // filter to ≥2.
        var allCollections = await _db.Collections
            .AsNoTracking()
            .Select(c => new
            {
                c.Id, c.Name, c.Overview, c.PosterUrl, c.WikidataId,
                Items = c.Items.Select(m => new { m.Id, m.LibraryId, m.PosterUrl, m.Type, m.ContentRating, m.IsMissing }).ToList(),
            })
            .ToListAsync();

        var visible = allCollections
            .Select(c => new
            {
                c.Id, c.Name, c.Overview, c.PosterUrl, c.WikidataId,
                Visible = c.Items.Where(i =>
                    !i.IsMissing && // SR-WI-011: missing movies don't count toward the >=2-visible threshold
                    (access.IsUnrestricted || access.AllowedLibraryIds.Contains(i.LibraryId)) &&
                    RatingFilterExtensions.IsRatingAllowed(ceilings, i.Type, i.ContentRating)).ToList(),
            })
            .Where(c => c.Visible.Count >= 2)
            .OrderBy(c => c.Name)
            .ToList();

        var dtos = visible.Select(c => new CollectionSummaryDto(
            c.Id,
            c.Name,
            c.Overview,
            // Fall back to first movie's poster when the collection itself
            // has none (manual collections without a custom poster).
            c.PosterUrl ?? c.Visible.FirstOrDefault(i => !string.IsNullOrEmpty(i.PosterUrl))?.PosterUrl,
            IsAuto: !string.IsNullOrEmpty(c.WikidataId),
            VisibleItemCount: c.Visible.Count
        )).ToList();

        return Ok(dtos);
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<CollectionDetailDto>> Get(Guid id)
    {
        var access = await _libraryAccess.GetCurrentAsync();
        var ceilings = await _ratings.GetCurrentAsync(); // audit wave-2 M-1
        var collection = await _db.Collections
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == id);
        if (collection == null) return NotFound();

        var movies = await _db.MediaItems
            .AsNoTracking()
            .Include(m => m.MediaItemGenres).ThenInclude(mg => mg.Genre)
            .Where(m => m.CollectionId == id && m.Type == MediaType.Movie)
            .ApplyContentRatingFilter(ceilings)
            .ExcludeMissing()
            .OrderBy(m => m.ReleaseDate)
                .ThenBy(m => m.Year)
                .ThenBy(m => m.Title)
            .ToListAsync();

        var visibleMovies = access.IsUnrestricted
            ? movies
            : movies.Where(m => access.AllowedLibraryIds.Contains(m.LibraryId)).ToList();

        // Hide the whole collection when the caller can't see any of its
        // members — same anti-probe posture as Wave C.
        if (visibleMovies.Count == 0) return NotFound();

        var entries = visibleMovies.Select(m => new CollectionEntryDto(
            MediaItemDto.FromMediaItem(m, "/api/v1/image/proxy"),
            IsCurrent: false)).ToList();

        return Ok(new CollectionDetailDto(
            collection.Id,
            collection.Name,
            collection.Overview,
            collection.PosterUrl ?? visibleMovies.FirstOrDefault(m => !string.IsNullOrEmpty(m.PosterUrl))?.PosterUrl,
            IsAuto: !string.IsNullOrEmpty(collection.WikidataId),
            entries));
    }

    /// <summary>
    /// Returns the collection siblings of a given movie for the
    /// "More from this collection" strip on the movie detail view.
    /// 204 when the movie has no collection or only the queried movie is visible.
    /// </summary>
    [HttpGet("by-movie/{movieId:guid}")]
    public async Task<IActionResult> GetByMovie(Guid movieId)
    {
        var access = await _libraryAccess.GetCurrentAsync();
        var ceilings = await _ratings.GetCurrentAsync(); // audit wave-2 M-1

        var movie = await _db.MediaItems
            .AsNoTracking()
            .Where(m => m.Id == movieId)
            .Select(m => new { m.Id, m.CollectionId, m.LibraryId, m.Type, m.ContentRating })
            .FirstOrDefaultAsync();

        if (movie == null) return NotFound();
        // Caller can't see the source movie at all (library ACL OR rating ceiling)? 404 (anti-probe).
        if (!access.IsUnrestricted && !access.AllowedLibraryIds.Contains(movie.LibraryId))
            return NotFound();
        if (!RatingFilterExtensions.IsRatingAllowed(ceilings, movie.Type, movie.ContentRating))
            return NotFound();
        if (movie.CollectionId == null) return NoContent();

        var collection = await _db.Collections
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == movie.CollectionId.Value);
        if (collection == null) return NoContent();

        var siblings = await _db.MediaItems
            .AsNoTracking()
            .Include(m => m.MediaItemGenres).ThenInclude(mg => mg.Genre)
            .Where(m => m.CollectionId == movie.CollectionId.Value && m.Type == MediaType.Movie)
            .ApplyContentRatingFilter(ceilings)
            .ExcludeMissing()
            .OrderBy(m => m.ReleaseDate)
                .ThenBy(m => m.Year)
                .ThenBy(m => m.Title)
            .ToListAsync();

        var visibleSiblings = access.IsUnrestricted
            ? siblings
            : siblings.Where(m => access.AllowedLibraryIds.Contains(m.LibraryId)).ToList();

        // Strip view rule: only render when ≥2 visible siblings exist (the
        // current movie counts as one). Otherwise the section would just
        // show the movie the user is already viewing — redundant.
        if (visibleSiblings.Count < 2) return NoContent();

        var entries = visibleSiblings.Select(m => new CollectionEntryDto(
            MediaItemDto.FromMediaItem(m, "/api/v1/image/proxy"),
            IsCurrent: m.Id == movieId)).ToList();

        return Ok(new CollectionDetailDto(
            collection.Id,
            collection.Name,
            collection.Overview,
            collection.PosterUrl ?? visibleSiblings.FirstOrDefault(m => !string.IsNullOrEmpty(m.PosterUrl))?.PosterUrl,
            IsAuto: !string.IsNullOrEmpty(collection.WikidataId),
            entries));
    }

    // ── Admin manual-collection management ────────────────────────────────────

    [HttpPost]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult<CollectionSummaryDto>> Create(CreateCollectionRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
            return BadRequest("Name is required.");
        if (request.Name.Length > 200)
            return BadRequest("Name exceeds 200 character limit.");

        var collection = new Collection
        {
            Name = request.Name.Trim(),
            Overview = string.IsNullOrWhiteSpace(request.Overview) ? null : request.Overview.Trim(),
            PosterUrl = string.IsNullOrWhiteSpace(request.PosterUrl) ? null : request.PosterUrl.Trim(),
            WikidataId = null, // manual
        };
        _db.Collections.Add(collection);

        if (request.MovieIds is { Count: > 0 })
        {
            var movies = await _db.MediaItems
                .Where(m => request.MovieIds.Contains(m.Id) && m.Type == MediaType.Movie)
                .ToListAsync();
            foreach (var m in movies) m.CollectionId = collection.Id;
        }

        await _db.SaveChangesAsync();

        return CreatedAtAction(nameof(Get), new { id = collection.Id },
            new CollectionSummaryDto(
                collection.Id, collection.Name, collection.Overview, collection.PosterUrl,
                IsAuto: false,
                VisibleItemCount: request.MovieIds?.Count ?? 0));
    }

    [HttpPatch("{id:guid}")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> Update(Guid id, UpdateCollectionRequest request)
    {
        var collection = await _db.Collections.FirstOrDefaultAsync(c => c.Id == id);
        if (collection == null) return NotFound();
        // Auto-collections are read-only — Wikidata is the source of truth.
        if (!string.IsNullOrEmpty(collection.WikidataId))
            return BadRequest("Auto-collections cannot be edited; create a manual collection instead.");

        if (request.Name != null)
        {
            var name = request.Name.Trim();
            if (string.IsNullOrEmpty(name)) return BadRequest("Name cannot be empty.");
            if (name.Length > 200) return BadRequest("Name exceeds 200 character limit.");
            collection.Name = name;
        }
        if (request.Overview != null)
            collection.Overview = string.IsNullOrWhiteSpace(request.Overview) ? null : request.Overview.Trim();
        if (request.PosterUrl != null)
            collection.PosterUrl = string.IsNullOrWhiteSpace(request.PosterUrl) ? null : request.PosterUrl.Trim();

        collection.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return NoContent();
    }

    [HttpPost("{id:guid}/items")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> AddItems(Guid id, AddCollectionItemsRequest request)
    {
        var collection = await _db.Collections.FirstOrDefaultAsync(c => c.Id == id);
        if (collection == null) return NotFound();
        if (!string.IsNullOrEmpty(collection.WikidataId))
            return BadRequest("Auto-collections cannot be edited; create a manual collection instead.");

        if (request.MovieIds is null || request.MovieIds.Count == 0)
            return BadRequest("MovieIds is required.");

        var movies = await _db.MediaItems
            .Where(m => request.MovieIds.Contains(m.Id) && m.Type == MediaType.Movie)
            .ToListAsync();
        foreach (var m in movies) m.CollectionId = id;

        collection.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return NoContent();
    }

    [HttpDelete("{id:guid}/items/{movieId:guid}")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> RemoveItem(Guid id, Guid movieId)
    {
        var collection = await _db.Collections.FirstOrDefaultAsync(c => c.Id == id);
        if (collection == null) return NotFound();
        if (!string.IsNullOrEmpty(collection.WikidataId))
            return BadRequest("Auto-collections cannot be edited; create a manual collection instead.");

        var movie = await _db.MediaItems.FirstOrDefaultAsync(m => m.Id == movieId && m.CollectionId == id);
        if (movie == null) return NotFound();
        movie.CollectionId = null;

        collection.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return NoContent();
    }

    [HttpDelete("{id:guid}")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> Delete(Guid id)
    {
        var collection = await _db.Collections.FirstOrDefaultAsync(c => c.Id == id);
        if (collection == null) return NotFound();
        // Auto-collections are read-only at the API surface. Re-scanning the
        // library would just re-create them anyway.
        if (!string.IsNullOrEmpty(collection.WikidataId))
            return BadRequest("Auto-collections cannot be deleted; disable EnableWikidataCollectionLookup to stop creating them.");

        // Movies' CollectionId is set null via FK ON DELETE SET NULL.
        _db.Collections.Remove(collection);
        await _db.SaveChangesAsync();
        return NoContent();
    }
}
