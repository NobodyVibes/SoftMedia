using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SoftMedia.Server.DTOs;
using SoftMedia.Server.Services.Identity;
using SoftMedia.Server.Services.Media;

namespace SoftMedia.Server.Controllers;

/// <summary>
/// Cross-library browse. Backs the "See more" link on home rows: each row describes
/// itself as a filter (see <c>HomeRowDto.Filter</c>) and this re-runs that filter with
/// paging, so the full grid is guaranteed to contain the same items as the row.
///
/// Deliberately separate from <c>/libraries/{id}/items</c>, which requires a library
/// and narrows by that library's type — it cannot express "every Comedy item on the
/// server". ACL and rating filtering happen inside <see cref="IBrowseService"/>.
/// </summary>
[ApiController]
[Authorize(Policy = ScopePolicies.ReadLibrary)]
[Route("api/v1/[controller]")]
public class BrowseController : ControllerBase
{
    private readonly IBrowseService _browse;

    public BrowseController(IBrowseService browse)
    {
        _browse = browse;
    }

    /// <param name="genre">Canonical genre name, matched case-insensitively.</param>
    /// <param name="decade">First year of a decade — 1990 selects 1990-1999.</param>
    /// <param name="unplayed">Only items the caller has never played (roll-up aware).</param>
    /// <param name="libraryId">Optional narrowing to a single library.</param>
    /// <param name="sortBy">title (default) | dateadded | year.</param>
    /// <param name="types">
    /// Comma-separated media types to narrow to, e.g. "Movie,Series". Unrecognised
    /// names are ignored rather than rejected: this drives a navigational link, and a
    /// stale client sending an unknown type should still land on a usable grid. The
    /// service clamps the result to browsable types either way.
    /// </param>
    [HttpGet]
    public async Task<ActionResult<PagedResult<MediaItemDto>>> Browse(
        [FromQuery] string? genre = null,
        [FromQuery] int? decade = null,
        [FromQuery] bool? unplayed = null,
        [FromQuery] Guid? libraryId = null,
        [FromQuery] string? sortBy = null,
        [FromQuery] string? sortDir = null,
        [FromQuery] string? types = null,
        [FromQuery] string? search = null,
        [FromQuery] int? year = null,
        [FromQuery] int? minRating = null,
        [FromQuery] bool? isFavorite = null,
        [FromQuery] bool? watched = null,
        [FromQuery] bool? inProgress = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        CancellationToken ct = default)
    {
        var idClaim = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier);
        if (idClaim == null || !Guid.TryParse(idClaim.Value, out var userId)) return Unauthorized();

        var parsedTypes = ParseTypes(types);

        var filter = new BrowseFilter
        {
            Genre = genre,
            Decade = decade,
            Unplayed = unplayed,
            LibraryId = libraryId,
            SortBy = sortBy,
            SortDir = sortDir,
            Types = parsedTypes.Length > 0 ? parsedTypes : null,
            Search = search,
            Year = year,
            MinRating = minRating,
            IsFavorite = isFavorite,
            Watched = watched,
            InProgress = inProgress,
            UserId = userId,
            // Clamped here, matching the house convention of bounding paging in the
            // controller before it reaches the service (see LibrariesController).
            Page = Math.Max(page, 1),
            PageSize = Math.Clamp(pageSize, 1, 100),
        };

        return Ok(await _browse.BrowseAsync(filter, ct));
    }

    /// <summary>
    /// Genre names available to the caller, for the browse page's genre picker.
    /// Optionally narrowed by media type so a video-only grid offers only video genres.
    /// </summary>
    [HttpGet("genres")]
    public async Task<ActionResult<IReadOnlyList<string>>> Genres(
        [FromQuery] string? types = null,
        CancellationToken ct = default)
    {
        var parsedTypes = ParseTypes(types);
        var filter = new BrowseFilter { Types = parsedTypes.Length > 0 ? parsedTypes : null };
        return Ok(await _browse.GetGenresAsync(filter, ct));
    }

    /// <summary>
    /// Parse a comma-separated media-type list. Unrecognised names are dropped rather
    /// than rejected — this drives navigational links, and a stale client sending an
    /// unknown type should still get a usable page. BrowseFilter clamps the result to
    /// browsable types regardless.
    /// </summary>
    private static Models.MediaType[] ParseTypes(string? types) =>
        (types ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(t => Enum.TryParse<Models.MediaType>(t, ignoreCase: true, out var parsed)
                ? (Models.MediaType?)parsed
                : null)
            .Where(t => t.HasValue)
            .Select(t => t!.Value)
            .ToArray();
}
