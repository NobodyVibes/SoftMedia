using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SoftMedia.Server.Data;
using SoftMedia.Server.DTOs;
using SoftMedia.Server.Extensions;
using SoftMedia.Server.Models;
using SoftMedia.Server.Services.Identity;
using SoftMedia.Server.Services.Media;
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
    private readonly ISmartPlaylistEvaluator _smartEvaluator;
    private readonly ILogger<PlaylistsController> _logger;

    public PlaylistsController(
        AppDbContext db,
        IUserLibraryAccessProvider libraryAccess,
        ISmartPlaylistEvaluator smartEvaluator,
        ILogger<PlaylistsController> logger)
    {
        _db = db;
        _libraryAccess = libraryAccess;
        _smartEvaluator = smartEvaluator;
        _logger = logger;
    }

    private static readonly JsonSerializerOptions RulesJson = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Rules off a stored row. Returns null for a manual playlist, and also for a
    /// smart row whose JSON no longer parses — a rules blob corrupted by hand or
    /// by a downgrade shouldn't 500 the whole index, so the playlist degrades to
    /// an empty result the owner can fix by re-saving it.
    /// </summary>
    private SmartPlaylistRules? ReadRules(Playlist playlist)
    {
        if (playlist.Kind != PlaylistKind.Smart || string.IsNullOrWhiteSpace(playlist.SmartRules))
            return null;
        try
        {
            return JsonSerializer.Deserialize<SmartPlaylistRules>(playlist.SmartRules, RulesJson);
        }
        catch (JsonException e)
        {
            _logger.LogWarning(e, "Playlist {PlaylistId} has unreadable smart rules", playlist.Id);
            return null;
        }
    }

    // ── List / read ──────────────────────────────────────────────────────────

    /// <summary>
    /// How many leading slots of a playlist are read when assembling its cover
    /// mosaic. Bounded so the list endpoint's cost stays proportional to the
    /// number of playlists, not to the size of the largest one — an 800-track
    /// playlist contributes the same 12 rows as a 12-track one.
    /// </summary>
    private const int CoverSourceDepth = 12;

    /// <summary>Tiles in the client's cover mosaic.</summary>
    private const int MaxCoversPerPlaylist = 4;

    [HttpGet]
    [Authorize(Policy = ScopePolicies.ReadState)] // B-18: playlists = user state
    public async Task<ActionResult<List<PlaylistSummaryDto>>> List()
    {
        var userId = User.GetUserId();
        var access = await _libraryAccess.GetCurrentAsync();

        // Own playlists + everyone else's public ones, most recently touched first.
        // The ordering is applied to the ENTITY, before the projection below: EF
        // cannot see through a constructor call to a record's property, so ordering
        // afterwards fails to translate (an anonymous type would have worked, which
        // is why this only surfaced against a real provider).
        var playlists = _db.Playlists
            .AsNoTracking()
            .Where(p => p.OwnerUserId == userId || p.IsPublic)
            .OrderByDescending(p => p.UpdatedAt);

        // The count must be drawn from the same population the detail endpoint
        // shows, which strips items the viewer's ACL blocks. Counting every row
        // instead put "12 tracks" on the card of a shared playlist that opened
        // with three — the card was reporting content the viewer cannot reach.
        var allowed = access.AllowedLibraryIds;
        var projected = access.IsUnrestricted
            ? playlists.Select(p => new PlaylistListRow(
                p.Id, p.Name, p.Description, p.IsPublic, p.OwnerUserId,
                p.Owner.Username, p.CreatedAt, p.UpdatedAt,
                p.Items.Count, p.Kind, p.SmartRules, p.CoverImagePath))
            : playlists.Select(p => new PlaylistListRow(
                p.Id, p.Name, p.Description, p.IsPublic, p.OwnerUserId,
                p.Owner.Username, p.CreatedAt, p.UpdatedAt,
                p.Items.Count(i => allowed.Contains(i.MediaItem.LibraryId)), p.Kind, p.SmartRules, p.CoverImagePath));

        var rows = await projected.ToListAsync();

        var covers = await LoadCoverMosaicsAsync(
            rows.Where(p => p.Kind == PlaylistKind.Manual).Select(p => p.Id).ToList(), access);

        var summaries = new List<PlaylistSummaryDto>(rows.Count);
        foreach (var p in rows)
        {
            var isOwner = p.OwnerUserId == userId;
            var count = p.ItemCount;
            var art = covers.TryGetValue(p.Id, out var manualArt) ? manualArt : new List<string>();
            SmartPlaylistRules? rules = null;

            if (p.Kind == PlaylistKind.Smart)
            {
                // A smart playlist stores nothing, so its count and artwork have to
                // be evaluated. Two bounded queries per smart playlist: the manual
                // path's single mosaic query can't serve them because there are no
                // PlaylistItem rows to read.
                rules = ReadRules(new Playlist { Id = p.Id, Kind = p.Kind, SmartRules = p.SmartRules });
                if (rules == null)
                {
                    count = 0;
                }
                else
                {
                    count = await _smartEvaluator.CountAsync(rules, p.OwnerUserId, access);
                    var preview = await _smartEvaluator.PreviewAsync(
                        rules, p.OwnerUserId, access, CoverSourceDepth);
                    art = preview
                        .Select(m => MediaItemDto.ResolvePosterPathFor(m, "/api/v1/image/proxy"))
                        .Where(path => !string.IsNullOrEmpty(path))
                        .Select(path => path!)
                        .Distinct()
                        .Take(MaxCoversPerPlaylist)
                        .ToList();
                }
            }

            // An uploaded cover replaces the mosaic outright: the client renders a
            // single path full-bleed, so no display code needs to know which it got.
            if (!string.IsNullOrEmpty(p.CoverImagePath)) art = new List<string> { p.CoverImagePath };

            summaries.Add(new PlaylistSummaryDto(
                p.Id, p.Name, p.Description, p.IsPublic,
                isOwner, p.OwnerUsername,
                count, p.CreatedAt, p.UpdatedAt, art,
                p.Kind,
                // Rules describe the owner's own listening — not a viewer's business.
                isOwner ? rules : null,
                p.CoverImagePath));
        }

        return Ok(summaries);
    }

    /// <summary>Flat projection of the list query; shaped for the summary DTO.</summary>
    private sealed record PlaylistListRow(
        Guid Id,
        string Name,
        string? Description,
        bool IsPublic,
        Guid OwnerUserId,
        string OwnerUsername,
        DateTime CreatedAt,
        DateTime UpdatedAt,
        int ItemCount,
        PlaylistKind Kind,
        string? SmartRules,
        // No default: this record is built inside EF expression trees, which
        // cannot call a constructor that relies on optional arguments.
        string? CoverImagePath);

    /// <summary>
    /// Distinct cover-art paths for each of the given playlists, in play order.
    /// Tracks resolve to their album's cover endpoint, so de-duplicating the
    /// resolved path is what makes a single-album playlist show one tile rather
    /// than the same sleeve four times.
    /// </summary>
    /// <param name="access">
    /// The caller's ACL, passed in rather than re-fetched: artwork is content, so
    /// a public playlist must not reveal covers from a library the viewer is denied.
    /// </param>
    private async Task<Dictionary<Guid, List<string>>> LoadCoverMosaicsAsync(
        List<Guid> playlistIds, LibraryAccess access)
    {
        if (playlistIds.Count == 0) return new Dictionary<Guid, List<string>>();

        var heads = await _db.PlaylistItems
            .AsNoTracking()
            .Where(pi => playlistIds.Contains(pi.PlaylistId) && pi.Order < CoverSourceDepth)
            .Include(pi => pi.MediaItem)
                .ThenInclude(m => m!.Album)
            .OrderBy(pi => pi.PlaylistId).ThenBy(pi => pi.Order)
            .ToListAsync();

        return heads
            .Where(pi => access.IsUnrestricted || access.AllowedLibraryIds.Contains(pi.MediaItem.LibraryId))
            .GroupBy(pi => pi.PlaylistId)
            .ToDictionary(
                g => g.Key,
                g => g.Select(pi => MediaItemDto.ResolvePosterPathFor(pi.MediaItem, "/api/v1/image/proxy"))
                      .Where(path => !string.IsNullOrEmpty(path))
                      .Select(path => path!)
                      .Distinct()
                      .Take(MaxCoversPerPlaylist)
                      .ToList());
    }

    /// <summary>
    /// Playlist matches for the global search box, by name or description.
    ///
    /// A separate endpoint rather than a branch of /media/search: that route
    /// groups <see cref="MediaItemDto"/> by library, and a playlist is neither a
    /// media item nor owned by a library. Squeezing one in would have meant
    /// faking a MediaItemDto whose id routes to /media/{id} — a page that does
    /// not exist for a playlist.
    ///
    /// Visibility matches <see cref="List"/>: the caller's own playlists plus
    /// everyone's public ones. Item counts and covers are deliberately NOT
    /// evaluated here — a search box types a character at a time, and running a
    /// smart playlist's rules on every keystroke is not worth a count in a
    /// dropdown row.
    /// </summary>
    [HttpGet("search")]
    [Authorize(Policy = ScopePolicies.ReadState)]
    public async Task<ActionResult<List<PlaylistSummaryDto>>> Search(
        [FromQuery] string query, [FromQuery] int limit = 5)
    {
        if (string.IsNullOrWhiteSpace(query) || query.Trim().Length < 2)
            return Ok(new List<PlaylistSummaryDto>());

        limit = Math.Clamp(limit, 1, 25);

        // LIKE wildcards in user input are live by default: "100%" would widen to
        // a prefix match and an interleaved-wildcard query is a superlinear scan.
        // Escaped exactly as MediaController.GlobalSearch does.
        var trimmed = query.Trim();
        if (trimmed.Length > 100) trimmed = trimmed[..100];
        var escaped = trimmed.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
        var pattern = $"%{escaped}%";
        var prefix = $"{escaped}%";
        const string Esc = "\\";

        var userId = User.GetUserId();

        var rows = await _db.Playlists
            .AsNoTracking()
            .Where(p => (p.OwnerUserId == userId || p.IsPublic)
                && (EF.Functions.Like(p.Name, pattern, Esc)
                    || (p.Description != null && EF.Functions.Like(p.Description, pattern, Esc))))
            // Name-prefix hits first, then any name hit, then description-only hits.
            .OrderBy(p => EF.Functions.Like(p.Name, prefix, Esc) ? 0
                        : EF.Functions.Like(p.Name, pattern, Esc) ? 1 : 2)
            .ThenByDescending(p => p.UpdatedAt)
            .Take(limit)
            .Select(p => new PlaylistListRow(
                p.Id, p.Name, p.Description, p.IsPublic, p.OwnerUserId,
                p.Owner.Username, p.CreatedAt, p.UpdatedAt, p.Items.Count, p.Kind, p.SmartRules,
                p.CoverImagePath))
            .ToListAsync();

        return Ok(rows.Select(p => new PlaylistSummaryDto(
            p.Id, p.Name, p.Description, p.IsPublic,
            p.OwnerUserId == userId, p.OwnerUsername,
            // Stored count for manual playlists; 0 for smart ones, whose real count
            // needs an evaluation this endpoint deliberately skips.
            p.Kind == PlaylistKind.Smart ? 0 : p.ItemCount,
            p.CreatedAt, p.UpdatedAt, new List<string>(), p.Kind, null)).ToList());
    }

    [HttpGet("{id:guid}")]
    [Authorize(Policy = ScopePolicies.ReadState)] // B-18: playlists = user state
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
        var isOwner = playlist.OwnerUserId == userId;

        if (playlist.Kind == PlaylistKind.Smart)
        {
            var rules = ReadRules(playlist);
            // Evaluated against the OWNER's signals but the VIEWER's library ACL:
            // the playlist means "the owner's favourites", yet must never reveal a
            // library this caller is denied.
            var tracks = rules == null
                ? new List<MediaItem>()
                : await _smartEvaluator.EvaluateAsync(rules, playlist.OwnerUserId, access);

            // No PlaylistItem rows exist, so the slot id is the track's own id. It
            // is stable and unique here because a smart playlist is a distinct
            // query — it cannot contain the same track twice, which is the only
            // reason manual playlists need a surrogate key.
            var smartItems = tracks
                .Select((m, idx) => new PlaylistEntryDto(
                    m.Id, idx, MediaItemDto.FromMediaItem(m, "/api/v1/image/proxy")))
                .ToList();

            return Ok(new PlaylistDetailDto(
                playlist.Id, playlist.Name, playlist.Description, playlist.IsPublic,
                isOwner, playlist.Owner.Username,
                playlist.CreatedAt, playlist.UpdatedAt, smartItems,
                playlist.Kind, isOwner ? rules : null, playlist.CoverImagePath));
        }

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
            isOwner,
            playlist.Owner.Username,
            playlist.CreatedAt,
            playlist.UpdatedAt,
            items,
            playlist.Kind,
            null,
            playlist.CoverImagePath));
    }

    // ── Create / update / delete ─────────────────────────────────────────────

    [HttpPost]
    [Authorize(Policy = ScopePolicies.WriteState)] // R-WI-006: read-only API tokens must not mutate
    public async Task<ActionResult<PlaylistSummaryDto>> Create(CreatePlaylistRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
            return BadRequest("Name is required.");
        if (request.Name.Length > 120)
            return BadRequest("Name exceeds 120 character limit.");

        var userId = User.GetUserId();
        var isSmart = request.Rules != null;

        if (isSmart)
        {
            if (request.IsPublic) return BadRequest(SmartPlaylistsArePrivate);
            var invalid = ValidateAndNormalize(request.Rules!);
            if (invalid != null) return BadRequest(invalid);
        }

        var playlist = new Playlist
        {
            OwnerUserId = userId,
            Name = request.Name.Trim(),
            Description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim(),
            IsPublic = !isSmart && request.IsPublic,
            Kind = isSmart ? PlaylistKind.Smart : PlaylistKind.Manual,
            SmartRules = isSmart ? JsonSerializer.Serialize(request.Rules, RulesJson) : null,
        };
        _db.Playlists.Add(playlist);
        await _db.SaveChangesAsync();

        var ownerUsername = await _db.Users.Where(u => u.Id == userId).Select(u => u.Username).FirstAsync();

        // A smart playlist has contents the moment it exists, so report its real
        // count rather than the 0 a freshly created manual playlist has.
        var count = 0;
        if (isSmart)
        {
            var access = await _libraryAccess.GetCurrentAsync();
            count = await _smartEvaluator.CountAsync(request.Rules!, userId, access);
        }

        return CreatedAtAction(nameof(Get), new { id = playlist.Id },
            new PlaylistSummaryDto(playlist.Id, playlist.Name, playlist.Description,
                playlist.IsPublic, true, ownerUsername, count, playlist.CreatedAt, playlist.UpdatedAt,
                new List<string>(), playlist.Kind, request.Rules));
    }

    /// <summary>
    /// Why a smart playlist cannot be shared. Its membership is computed from the
    /// owner's favourites and play history, so "public" would mean either exposing
    /// one user's listening signals to everyone, or silently showing each viewer a
    /// different list from the same playlist. Both are worse than not offering it;
    /// a rules set with no personal signals could be allowed later.
    /// </summary>
    private const string SmartPlaylistsArePrivate =
        "Smart playlists are private: their contents are derived from the owner's own favourites and listening history.";

    /// <summary>Validates then canonicalises in place. Returns an error, or null when usable.</summary>
    private static string? ValidateAndNormalize(SmartPlaylistRules rules)
    {
        var error = rules.Validate();
        if (error != null) return error;
        rules.Normalize();
        return null;
    }

    [HttpPatch("{id:guid}")]
    [Authorize(Policy = ScopePolicies.WriteState)] // R-WI-006
    public async Task<IActionResult> Update(Guid id, UpdatePlaylistRequest request)
    {
        var userId = User.GetUserId();
        var playlist = await _db.Playlists.FirstOrDefaultAsync(p => p.Id == id);
        if (playlist == null) return NotFound();
        if (playlist.OwnerUserId != userId) return NotFound(); // anti-probe per SDD §6.2

        // Validate everything before mutating anything: a request that sets a valid
        // name and invalid rules must not persist half of itself.
        if (request.Rules != null)
        {
            if (playlist.Kind != PlaylistKind.Smart)
                return BadRequest("This playlist is not a smart playlist; its rules cannot be set.");
            var invalidRules = ValidateAndNormalize(request.Rules);
            if (invalidRules != null) return BadRequest(invalidRules);
        }
        if (request.IsPublic == true && playlist.Kind == PlaylistKind.Smart)
            return BadRequest(SmartPlaylistsArePrivate);

        if (request.Name != null)
        {
            var name = request.Name.Trim();
            if (string.IsNullOrEmpty(name)) return BadRequest("Name cannot be empty.");
            if (name.Length > 120) return BadRequest("Name exceeds 120 character limit.");
            playlist.Name = name;
        }
        if (request.Rules != null)
        {
            playlist.SmartRules = JsonSerializer.Serialize(request.Rules, RulesJson);
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
    [Authorize(Policy = ScopePolicies.WriteState)] // R-WI-006
    public async Task<IActionResult> Delete(
        Guid id, [FromServices] IPlaylistCoverService covers)
    {
        var userId = User.GetUserId();
        var playlist = await _db.Playlists.FirstOrDefaultAsync(p => p.Id == id);
        if (playlist == null) return NotFound();
        if (playlist.OwnerUserId != userId) return NotFound();

        var hadCover = playlist.CoverImagePath != null;

        _db.Playlists.Remove(playlist);
        await _db.SaveChangesAsync();

        // Only after the row is gone: an uploaded cover outlives nothing, and
        // deleting the file first would strand the playlist coverless if the
        // delete then failed.
        if (hadCover) covers.Delete(id);

        return NoContent();
    }

    // ── Custom cover ─────────────────────────────────────────────────────────

    /// <summary>
    /// Replaces the playlist's generated cover mosaic with an uploaded image.
    ///
    /// The upload is decoded and re-encoded by
    /// <see cref="IPlaylistCoverService"/> before anything reaches disk, so the
    /// stored file is always our own WebP — never the client's bytes, never the
    /// client's filename.
    /// </summary>
    [HttpPost("{id:guid}/cover")]
    [Authorize(Policy = ScopePolicies.WriteState)]
    [RequestSizeLimit(PlaylistCoverService.MaxUploadBytes + 1024)]
    public async Task<IActionResult> UploadCover(
        Guid id, IFormFile file,
        [FromServices] IPlaylistCoverService covers,
        CancellationToken ct)
    {
        var userId = User.GetUserId();
        var playlist = await _db.Playlists.FirstOrDefaultAsync(p => p.Id == id, ct);
        if (playlist == null) return NotFound();
        if (playlist.OwnerUserId != userId) return NotFound();

        if (file == null || file.Length == 0) return BadRequest("No image was uploaded.");

        await using var stream = file.OpenReadStream();
        var result = await covers.SaveAsync(id, stream, ct);
        if (!result.Success) return BadRequest(result.Error);

        playlist.CoverImagePath = result.RelativePath;
        playlist.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        return Ok(new { coverImagePath = playlist.CoverImagePath });
    }

    /// <summary>Drops the uploaded cover; the playlist returns to its track mosaic.</summary>
    [HttpDelete("{id:guid}/cover")]
    [Authorize(Policy = ScopePolicies.WriteState)]
    public async Task<IActionResult> DeleteCover(
        Guid id, [FromServices] IPlaylistCoverService covers)
    {
        var userId = User.GetUserId();
        var playlist = await _db.Playlists.FirstOrDefaultAsync(p => p.Id == id);
        if (playlist == null) return NotFound();
        if (playlist.OwnerUserId != userId) return NotFound();

        covers.Delete(id);
        playlist.CoverImagePath = null;
        playlist.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        return NoContent();
    }

    // ── M3U interchange ──────────────────────────────────────────────────────

    /// <summary>
    /// The playlist as an extended M3U download.
    ///
    /// Entries are the tracks' paths ON THE SERVER, which is what every other
    /// media server exports and what makes the file useful to a local player
    /// pointed at the same library. A playlist exported here and opened on a
    /// machine that mounts the library elsewhere will not resolve by path — the
    /// import side compensates by falling back to filenames.
    ///
    /// Smart playlists export the snapshot they currently evaluate to, which is
    /// the only sensible reading of "export a query" for a file format that has
    /// no way to express one.
    /// </summary>
    [HttpGet("{id:guid}/export")]
    [Authorize(Policy = ScopePolicies.ReadState)]
    public async Task<IActionResult> Export(Guid id)
    {
        var userId = User.GetUserId();
        var playlist = await _db.Playlists.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id);
        if (playlist == null) return NotFound();
        if (playlist.OwnerUserId != userId && !playlist.IsPublic) return NotFound();

        var access = await _libraryAccess.GetCurrentAsync();
        List<MediaItem> tracks;

        if (playlist.Kind == PlaylistKind.Smart)
        {
            var rules = ReadRules(playlist);
            tracks = rules == null
                ? new List<MediaItem>()
                : await _smartEvaluator.EvaluateAsync(rules, playlist.OwnerUserId, access);
        }
        else
        {
            var entries = await _db.PlaylistItems
                .AsNoTracking()
                .Where(pi => pi.PlaylistId == id)
                .Include(pi => pi.MediaItem).ThenInclude(m => m!.Artist)
                .OrderBy(pi => pi.Order)
                .ToListAsync();

            tracks = entries
                .Where(e => access.IsUnrestricted || access.AllowedLibraryIds.Contains(e.MediaItem.LibraryId))
                .Select(e => e.MediaItem)
                .ToList();
        }

        var content = M3uPlaylistFormat.Write(
            playlist.Name,
            tracks.Select(t => new M3uTrack(
                t.Path, t.Title, t.Artist?.Title, (int)Math.Round(t.Duration))));

        return File(System.Text.Encoding.UTF8.GetBytes(content), "audio/x-mpegurl", $"{SafeFileName(playlist.Name)}.m3u");
    }

    /// <summary>
    /// Creates a playlist from an uploaded M3U.
    ///
    /// Matching is by exact path first, then by filename — a playlist written on
    /// another machine has the right filenames but the wrong prefixes, and that
    /// is the common case for importing at all. Lines that match nothing are
    /// reported rather than silently dropped, because a half-imported playlist
    /// that claims success is worse than one that says what it missed.
    /// </summary>
    [HttpPost("import")]
    [Authorize(Policy = ScopePolicies.WriteState)]
    public async Task<ActionResult<ImportPlaylistResultDto>> Import(ImportPlaylistRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Content))
            return BadRequest("The playlist file is empty.");
        if (System.Text.Encoding.UTF8.GetByteCount(request.Content) > M3uPlaylistFormat.MaxContentBytes)
            return BadRequest("That playlist file is too large to import.");

        var paths = M3uPlaylistFormat.ParsePaths(request.Content);
        if (paths.Count == 0)
            return BadRequest("No track entries were found in that file.");

        var name = (request.Name ?? M3uPlaylistFormat.ParseName(request.Content) ?? "Imported Playlist").Trim();
        if (name.Length == 0) name = "Imported Playlist";
        if (name.Length > 120) name = name[..120];

        var userId = User.GetUserId();
        var access = await _libraryAccess.GetCurrentAsync();

        // One projection of the audio the caller may see. Import is a rare,
        // explicit action, so a single pass beats issuing a query per line.
        var candidates = await _db.MediaItems
            .AsNoTracking()
            .ApplyLibraryAccessFilter(access)
            .ExcludeMissing()
            .Where(m => m.Type == MediaType.Audio)
            .Select(m => new { m.Id, m.Path })
            .ToListAsync();

        // Three indexes, weakest last. "Ambiguous" entries are recorded as such and
        // then refused: two library files sharing a key give no way to tell which
        // the playlist meant, and silently importing the wrong track is worse than
        // reporting the line as unmatched. This matters most for the bare filename —
        // "01.mp3" exists in practically every album folder.
        var byPath = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        var byTail = new Dictionary<string, Guid?>(StringComparer.OrdinalIgnoreCase);
        var byFileName = new Dictionary<string, Guid?>(StringComparer.OrdinalIgnoreCase);

        static void Index(Dictionary<string, Guid?> index, string key, Guid id)
        {
            if (index.TryGetValue(key, out var existing))
            {
                if (existing != id) index[key] = null; // now ambiguous
            }
            else index[key] = id;
        }

        foreach (var c in candidates)
        {
            byPath.TryAdd(c.Path, c.Id);
            Index(byTail, M3uPlaylistFormat.TailOf(c.Path), c.Id);
            Index(byFileName, M3uPlaylistFormat.FileNameOf(c.Path), c.Id);
        }

        var matched = new List<Guid>();
        var unmatched = new List<string>();
        foreach (var path in paths)
        {
            if (byPath.TryGetValue(path, out var exact))
            {
                matched.Add(exact);
            }
            else if (byTail.TryGetValue(M3uPlaylistFormat.TailOf(path), out var tail) && tail.HasValue)
            {
                matched.Add(tail.Value);
            }
            else if (byFileName.TryGetValue(M3uPlaylistFormat.FileNameOf(path), out var fileName) && fileName.HasValue)
            {
                matched.Add(fileName.Value);
            }
            else unmatched.Add(path);
        }

        if (matched.Count == 0)
        {
            return BadRequest(
                "None of the tracks in that playlist are in your library. " +
                "If it came from another machine, the file names still have to match.");
        }

        var playlist = new Playlist { OwnerUserId = userId, Name = name };
        _db.Playlists.Add(playlist);
        await _db.SaveChangesAsync();

        for (var i = 0; i < matched.Count; i++)
        {
            _db.PlaylistItems.Add(new PlaylistItem
            {
                PlaylistId = playlist.Id, MediaItemId = matched[i], Order = i,
            });
        }
        await _db.SaveChangesAsync();

        var ownerUsername = await _db.Users.Where(u => u.Id == userId).Select(u => u.Username).FirstAsync();

        return Ok(new ImportPlaylistResultDto(
            new PlaylistSummaryDto(playlist.Id, playlist.Name, playlist.Description, playlist.IsPublic,
                true, ownerUsername, matched.Count, playlist.CreatedAt, playlist.UpdatedAt,
                new List<string>(), playlist.Kind, null),
            matched.Count,
            unmatched.Count,
            unmatched.Take(5).ToList()));
    }

    /// <summary>Strips anything that cannot appear in a download filename.</summary>
    private static string SafeFileName(string name)
    {
        var cleaned = new string(name
            .Select(c => System.IO.Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)
            .ToArray()).Trim();
        return cleaned.Length == 0 ? "playlist" : cleaned;
    }

    // ── Items: append, remove, reorder ───────────────────────────────────────

    /// <summary>
    /// Why the membership endpoints refuse a smart playlist. Its contents are a
    /// query re-run on every read, so an inserted, removed or reordered row would
    /// simply vanish at the next fetch — failing loudly beats appearing to work.
    /// </summary>
    private const string SmartMembershipIsDerived =
        "This is a smart playlist: its tracks are derived from its rules. Edit the rules instead.";

    [HttpPost("{id:guid}/items")]
    [Authorize(Policy = ScopePolicies.WriteState)] // R-WI-006
    public async Task<IActionResult> AddItems(Guid id, AddPlaylistItemsRequest request)
    {
        var userId = User.GetUserId();
        var playlist = await _db.Playlists.FirstOrDefaultAsync(p => p.Id == id);
        if (playlist == null) return NotFound();
        if (playlist.OwnerUserId != userId) return NotFound();

        if (playlist.Kind == PlaylistKind.Smart) return BadRequest(SmartMembershipIsDerived);

        if (request.MediaItemIds == null || request.MediaItemIds.Count == 0)
            return BadRequest("MediaItemIds is required.");

        // v1 scope: audio tracks only. Reject non-audio explicitly so the
        // client gets a clean 400 instead of a silent "added but won't play".
        // Audit wave-2 L-2: filter through the per-library ACL so a restricted user can't attach
        // tracks from a library they're denied (the id set leaks via shared/public content).
        var access = await _libraryAccess.GetCurrentAsync();
        var requested = request.MediaItemIds.Distinct().ToList();
        var allowed = await _db.MediaItems
            .ApplyLibraryAccessFilter(access)
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
    [Authorize(Policy = ScopePolicies.WriteState)] // R-WI-006
    public async Task<IActionResult> RemoveItem(Guid id, Guid itemId)
    {
        var userId = User.GetUserId();
        var playlist = await _db.Playlists.FirstOrDefaultAsync(p => p.Id == id);
        if (playlist == null) return NotFound();
        if (playlist.OwnerUserId != userId) return NotFound();

        if (playlist.Kind == PlaylistKind.Smart) return BadRequest(SmartMembershipIsDerived);

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
    [Authorize(Policy = ScopePolicies.WriteState)] // R-WI-006
    public async Task<IActionResult> Reorder(Guid id, ReorderPlaylistRequest request)
    {
        var userId = User.GetUserId();
        var playlist = await _db.Playlists.FirstOrDefaultAsync(p => p.Id == id);
        if (playlist == null) return NotFound();
        if (playlist.OwnerUserId != userId) return NotFound();

        if (playlist.Kind == PlaylistKind.Smart) return BadRequest(SmartMembershipIsDerived);

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
