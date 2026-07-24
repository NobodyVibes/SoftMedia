using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SoftMedia.Server.Data;
using SoftMedia.Server.Models;
using SoftMedia.Server.Services.Identity;
using SoftMedia.Server.Services.Infrastructure;
using SoftMedia.Server.Services.Media;
using SoftMedia.Server.Services.Scanning;
using SoftMedia.Server.Services.Transcoding;
using SoftMedia.Server.Services.Metadata;
using SoftMedia.Server.Services.Abstractions;

namespace SoftMedia.Server.Controllers;

/// <summary>
/// Admin-only endpoints for diagnostics and system management.
/// </summary>
[ApiController]
[Route("api/v1/[controller]")]
[Authorize(Roles = "Admin")]
public class AdminController : ControllerBase
{
    private readonly LibraryWatcher _libraryWatcher;
    private readonly ILogger<AdminController> _logger;
    private readonly IEnumerable<IMetadataProvider> _providers;
    private readonly IRecommendationService _recommendationService;
    private readonly IBackupService _backupService;
    private readonly IScheduledTaskRegistry _taskRegistry;
    private readonly IEnumerable<IManuallyTriggerableTask> _triggerableTasks;

    public AdminController(
        LibraryWatcher libraryWatcher,
        ILogger<AdminController> logger,
        IEnumerable<IMetadataProvider> providers,
        IRecommendationService recommendationService,
        IBackupService backupService,
        IScheduledTaskRegistry taskRegistry,
        IEnumerable<IManuallyTriggerableTask> triggerableTasks)
    {
        _libraryWatcher = libraryWatcher;
        _logger = logger;
        _providers = providers;
        _recommendationService = recommendationService;
        _backupService = backupService;
        _taskRegistry = taskRegistry;
        _triggerableTasks = triggerableTasks;
    }

    /// <summary>
    /// Gets all current file watcher issues.
    /// </summary>
    [HttpGet("file-watcher-issues")]
    public ActionResult<IEnumerable<FileWatcherIssue>> GetFileWatcherIssues()
    {
        var issues = _libraryWatcher.GetFileIssues();
        return Ok(issues);
    }

    /// <summary>
    /// Retries a file that previously had issues.
    /// </summary>
    [HttpPost("file-watcher-issues/retry")]
    public ActionResult RetryFile([FromBody] RetryFileRequest request)
    {
        if (string.IsNullOrEmpty(request.Path))
        {
            return BadRequest("Path is required");
        }

        var success = _libraryWatcher.RetryFile(request.Path);
        if (!success)
        {
            return NotFound("File issue not found or file no longer exists");
        }

        _logger.LogInformation("Admin retried file: {Path}", request.Path);
        return Ok(new { success = true });
    }

    /// <summary>
    /// Clears/dismisses a file watcher issue.
    /// </summary>
    [HttpDelete("file-watcher-issues")]
    public ActionResult ClearIssue([FromQuery] string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return BadRequest("Path is required");
        }

        var success = _libraryWatcher.ClearIssue(path);
        if (!success)
        {
            return NotFound("File issue not found");
        }

        _logger.LogInformation("Admin cleared file issue: {Path}", path);
        return Ok(new { success = true });
    }

    /// <summary>
    /// Gets OMDb API usage information.
    /// </summary>
    [HttpGet("omdb-usage")]
    public async Task<IActionResult> GetOMDbUsage()
    {
        var omdbProvider = _providers.OfType<OMDbProvider>().FirstOrDefault();
        if (omdbProvider == null)
        {
            return NotFound("OMDb provider not registered");
        }

        var (used, limit, tier, isExhausted) = await omdbProvider.GetUsageInfoAsync();
        return Ok(new
        {
            used,
            limit,
            tier,
            isExhausted,
            resetTimeUtc = DateTime.UtcNow.Date.AddDays(1).ToString("yyyy-MM-ddTHH:mm:ssZ")
        });
    }

    /// <summary>
    /// Manually triggers an update of the hero section cache.
    /// </summary>
    [HttpPost("hero-cache/refresh")]
    public async Task<IActionResult> RefreshHeroCache()
    {
        // Report to the task registry so the Background Tasks card reflects manual runs
        // too (the daily HeroCacheWorker reports on its schedule; this is the on-demand path).
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            await _recommendationService.UpdateHeroCacheAsync();
            _taskRegistry.Report(ScheduledTaskNames.HeroCache, "Success", sw.ElapsedMilliseconds);
            return Ok(new { success = true, message = "Hero cache refresh completed" });
        }
        catch (Exception ex)
        {
            _taskRegistry.Report(ScheduledTaskNames.HeroCache, "Failed", sw.ElapsedMilliseconds, ex.Message);
            throw;
        }
    }

    /// <summary>
    /// Manually enqueue intro/credits detection for a single series. Returns the
    /// queued (or already-running) job so the admin UI can poll its status.
    /// </summary>
    [HttpPost("series/{seriesId:guid}/detect-intros")]
    public async Task<IActionResult> EnqueueIntroCreditsDetection(
        Guid seriesId,
        [FromServices] AppDbContext db,
        [FromServices] ILibraryScanQueueService queue)
    {
        var series = await db.MediaItems
            .Where(m => m.Id == seriesId && m.Type == MediaType.Series)
            .Select(m => new { m.Id, m.Title })
            .FirstOrDefaultAsync();

        if (series == null) return NotFound("Series not found");

        var episodeCount = await db.MediaItems.CountAsync(m => m.SeriesId == seriesId && m.Type == MediaType.Episode);
        if (episodeCount < 2)
        {
            return BadRequest(new { error = "Series needs at least 2 episodes for cross-episode detection." });
        }

        var job = queue.EnqueueIntroCreditsDetection(series.Id, series.Title);
        _logger.LogInformation("Admin enqueued intro/credits detection for series {SeriesId} ({Title})", series.Id, series.Title);

        return Ok(new { jobId = job.Id, status = job.Status.ToString() });
    }

    // --- Backup / Restore (P1-WI-001) ---

    /// <summary>Creates a backup on the server and returns its metadata. An optional
    /// display name can be supplied in the body.</summary>
    [HttpPost("backup")]
    public async Task<IActionResult> CreateBackup(
        [FromBody(EmptyBodyBehavior = Microsoft.AspNetCore.Mvc.ModelBinding.EmptyBodyBehavior.Allow)] BackupNameRequest? request,
        CancellationToken ct)
    {
        var info = await _backupService.CreateBackupAsync(request?.Name, ct);
        _logger.LogInformation("Admin {User} created backup {Id}", User.Identity?.Name, info.Id);
        return Ok(info);
    }

    /// <summary>Lists backups on disk, newest first.</summary>
    [HttpGet("backup")]
    public async Task<IActionResult> ListBackups(CancellationToken ct)
        => Ok(await _backupService.ListBackupsAsync(ct));

    /// <summary>Renames a backup's display label (the archive/id is unchanged). A blank
    /// name reverts the label to the id.</summary>
    [HttpPatch("backup/{id}/name")]
    public async Task<IActionResult> RenameBackup(string id, [FromBody] BackupNameRequest request, CancellationToken ct)
    {
        if (!await _backupService.SetBackupNameAsync(id, request?.Name, ct))
            return NotFound("Backup not found.");
        _logger.LogInformation("Admin {User} renamed backup {Id}", User.Identity?.Name, id);
        return Ok();
    }

    /// <summary>Permanently deletes a backup archive (and its pin/name markers).</summary>
    [HttpDelete("backup/{id}")]
    public async Task<IActionResult> DeleteBackup(string id, CancellationToken ct)
    {
        if (!await _backupService.DeleteBackupAsync(id, ct))
            return NotFound("Backup not found.");
        _logger.LogWarning("Admin {User} deleted backup {Id}", User.Identity?.Name, id);
        return NoContent();
    }

    /// <summary>Downloads a backup archive by id.</summary>
    [HttpGet("backup/{id}/download")]
    public async Task<IActionResult> DownloadBackup(string id, CancellationToken ct)
    {
        var stream = await _backupService.OpenBackupAsync(id, ct);
        if (stream == null) return NotFound("Backup not found.");
        return File(stream, "application/zip", id + ".zip");
    }

    /// <summary>Pins a backup so rotation never deletes it.</summary>
    [HttpPost("backup/{id}/pin")]
    public async Task<IActionResult> PinBackup(string id, CancellationToken ct)
        => await _backupService.SetPinnedAsync(id, true, ct) ? Ok() : NotFound("Backup not found.");

    /// <summary>Unpins a backup, making it eligible for rotation again.</summary>
    [HttpDelete("backup/{id}/pin")]
    public async Task<IActionResult> UnpinBackup(string id, CancellationToken ct)
        => await _backupService.SetPinnedAsync(id, false, ct) ? Ok() : NotFound("Backup not found.");

    /// <summary>
    /// Uploads a backup archive and stages its database for restore on next restart.
    /// Non-destructive in-process: the swap happens before the DB opens on next boot.
    /// </summary>
    [HttpPost("restore")]
    [RequestSizeLimit(2L * 1024 * 1024 * 1024)] // 2 GiB ceiling for the upload
    public async Task<IActionResult> Restore(IFormFile file, CancellationToken ct)
    {
        if (file == null || file.Length == 0) return BadRequest("No backup file uploaded.");

        await using var stream = file.OpenReadStream();
        var result = await _backupService.StageRestoreAsync(stream, ct);
        if (!result.Success) return BadRequest(result);

        _logger.LogWarning("Admin {User} staged a database restore.", User.Identity?.Name);
        return Accepted(result);
    }

    /// <summary>
    /// Re-fetches artwork that a database-only restore could not bring back. Backups
    /// exclude the on-disk image cache, so restored rows point at /cache/ files that no
    /// longer exist; this re-queues the affected items for metadata enrichment. Runs
    /// automatically once after a restore, and on demand here.
    /// </summary>
    [HttpPost("repair-artwork")]
    public async Task<IActionResult> RepairArtwork(
        [FromServices] IArtworkRepairService artworkRepair, CancellationToken ct)
    {
        var result = await artworkRepair.RepairAsync(ct);
        _logger.LogInformation(
            "Admin {User} ran artwork repair: re-queued {ReEnqueued}, missing {Missing}, locked {Locked}, rescan {Rescan}",
            User.Identity?.Name, result.ItemsReEnqueued, result.MissingImages, result.LockedSkipped, result.NeedsRescan);
        return Ok(result);
    }

    /// <summary>
    /// Collapses the Genre table onto its canonical form: merges case-variants
    /// ("Science Fiction" / "science fiction"), splits BISAC subject paths that book
    /// providers send as one string, and drops non-genre headings (bare years,
    /// "Dune (Imaginary place)").
    ///
    /// Defaults to a DRY RUN — it reports what would change and writes nothing.
    /// Pass dryRun=false to apply. Idempotent: re-running on clean data is a no-op.
    /// Aborts rather than leaving any item with zero genres.
    /// </summary>
    [HttpPost("normalize-genres")]
    public async Task<IActionResult> NormalizeGenres(
        [FromServices] Services.Media.IGenreMaintenanceService genreMaintenance,
        [FromQuery] bool dryRun = true,
        CancellationToken ct = default)
    {
        var result = await genreMaintenance.NormalizeAsync(dryRun, ct);
        _logger.LogInformation(
            "Admin {User} ran genre normalisation (dryRun={DryRun}): {Before} -> {After} genres",
            User.Identity?.Name, dryRun, result.GenresBefore, result.GenresAfter);
        return Ok(result);
    }

    // --- Scheduled tasks (P1-WI-005) ---

    /// <summary>Lists all background tasks with their last-run telemetry.</summary>
    [HttpGet("tasks")]
    public IActionResult GetTasks() => Ok(_taskRegistry.GetAll());

    /// <summary>
    /// Manually triggers a task that supports it (any service registered as
    /// <see cref="IManuallyTriggerableTask"/> — R-WI-008 generalised the previously
    /// metadata-refresh-only dispatch). Unknown/unsupported task names return 400.
    /// Triggering is fire-and-forget; the result is reflected on the next GET /tasks.
    /// </summary>
    [HttpPost("tasks/{name}/trigger")]
    public IActionResult TriggerTask(string name)
    {
        var task = _triggerableTasks.FirstOrDefault(t => string.Equals(t.TaskName, name, StringComparison.Ordinal));
        if (task == null)
        {
            return BadRequest($"Task '{name}' does not support manual triggering.");
        }

        task.TriggerNow();
        _logger.LogInformation("Admin {User} manually triggered {Task}", User.Identity?.Name, name);
        return Accepted(new { message = $"{name} triggered." });
    }

    /// <summary>
    /// Admin recovery: disables 2FA for a user who is locked out (lost device + recovery
    /// codes). The only out-of-band reset path — there is intentionally no self-service
    /// bypass (P2-WI-005).
    /// </summary>
    [HttpPost("users/{userId:guid}/disable-2fa")]
    public async Task<IActionResult> DisableUserTwoFactor(
        Guid userId, [FromServices] AppDbContext db, [FromServices] ITrustedDeviceService trustedDevices)
    {
        // The admin recovery path intentionally requires no target password — so it must
        // never be used to strip your *own* 2FA without one. Self-removal must go through the
        // password-protected self-service flow (POST /account/totp/disable).
        var callerId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (Guid.TryParse(callerId, out var callerGuid) && callerGuid == userId)
            return BadRequest("To disable your own 2FA, use Account settings — it requires your password.");

        var totp = await db.UserTotps.FirstOrDefaultAsync(t => t.UserId == userId);
        if (totp == null) return NotFound("User has no 2FA enrollment.");
        db.UserTotps.Remove(totp);
        await db.SaveChangesAsync();
        // Also forget any remembered devices so a reset is clean.
        await trustedDevices.RevokeAllAsync(userId);
        _logger.LogWarning("Admin {Admin} disabled 2FA for user {UserId}", User.Identity?.Name, userId);
        return Ok();
    }

    // --- Manual metadata fix (P3-WI-003) ---

    /// <summary>
    /// Looks up the searchable provider(s) that handle the item's library type and
    /// runs each one with the supplied query. Returns merged ranked candidates so the
    /// admin can pick the right match. Movies have two providers (Wikidata + OMDb);
    /// other types have one each.
    /// </summary>
    [HttpPost("match/{itemId:guid}/search")]
    public async Task<IActionResult> SearchMatch(
        Guid itemId,
        [FromBody] SearchMatchRequest request,
        [FromServices] AppDbContext db,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Query)) return BadRequest("Query is required.");

        var item = await db.MediaItems.Include(m => m.Library).FirstOrDefaultAsync(m => m.Id == itemId, ct);
        if (item == null || item.Library == null) return NotFound("Media item not found.");

        var searchable = _providers
            .OfType<ISearchableMetadataProvider>()
            .Where(p => p.SupportedType == item.Library.Type)
            .ToList();
        if (searchable.Count == 0)
            return BadRequest($"No searchable metadata provider is configured for library type '{item.Library.Type}'.");

        var all = new List<MetadataSearchCandidate>();
        foreach (var p in searchable)
        {
            try
            {
                var candidates = await p.SearchAsync(request.Query, request.Year, ct);
                all.AddRange(candidates);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Provider {Provider} search failed for '{Query}'", p.ProviderName, request.Query);
            }
        }
        return Ok(all);
    }

    /// <summary>
    /// Applies a chosen candidate: fetches full metadata from that provider, writes it
    /// over the MediaItem, sets MetadataLocked so the auto-refresh won't undo it.
    /// </summary>
    [HttpPost("match/{itemId:guid}/apply")]
    public async Task<IActionResult> ApplyMatch(
        Guid itemId,
        [FromBody] ApplyMatchRequest request,
        [FromServices] AppDbContext db,
        CancellationToken ct)
    {
        var item = await db.MediaItems.Include(m => m.Library).FirstOrDefaultAsync(m => m.Id == itemId, ct);
        if (item == null || item.Library == null) return NotFound();

        var provider = _providers.OfType<ISearchableMetadataProvider>()
            .FirstOrDefault(p => p.ProviderName == request.ProviderName && p.SupportedType == item.Library.Type);
        if (provider == null) return BadRequest($"Provider '{request.ProviderName}' is not available for this library type.");

        var result = await provider.FetchByCandidateAsync(request.ProviderItemId, ct);
        if (result == null) return BadRequest("Provider returned no metadata for the chosen candidate.");

        // Overwrite only the fields the provider actually produced; preserve existing
        // user state (PlayCount, IsFavorite, watch progress are NOT on MediaItem itself).
        if (!string.IsNullOrEmpty(result.Title)) item.Title = result.Title!;
        if (!string.IsNullOrEmpty(result.Description)) item.Overview = result.Description;
        if (result.Year.HasValue) item.Year = result.Year;
        if (!string.IsNullOrEmpty(result.PosterUrl))
        {
            item.PosterUrl = result.PosterUrl;
            // R-WI-014: an explicit admin fix-match replaces local art — clear the local claim
            // or, after unlock, the stale flag would suppress provider posters forever.
            item.PosterFromLocalFile = false;
        }
        if (!string.IsNullOrEmpty(result.Director)) item.Director = result.Director;
        if (!string.IsNullOrEmpty(result.ContentRating)) item.ContentRating = result.ContentRating;

        // External IDs — set whichever the provider returned so future fetches short-circuit.
        if (!string.IsNullOrEmpty(result.ImdbId)) item.ImdbId = result.ImdbId;
        if (!string.IsNullOrEmpty(result.MusicBrainzId)) item.MusicBrainzId = result.MusicBrainzId;
        if (result.TvMazeId.HasValue) item.TvMazeId = result.TvMazeId;

        item.MetadataLocked = true;
        item.MetadataLockedAt = DateTime.UtcNow;

        // SR-WI-036: an explicit admin match supersedes retry exhaustion — clear the flag and
        // any pending retry row so a later unlock lets auto-refresh work again instead of
        // being silently blocked by the stale exhausted state.
        item.IsRetryExhausted = false;
        var pendingRetries = await db.MetadataRetries.Where(r => r.MediaItemId == itemId).ToListAsync(ct);
        db.MetadataRetries.RemoveRange(pendingRetries);

        await db.SaveChangesAsync(ct);

        _logger.LogInformation("Admin {Admin} fix-matched item {ItemId} via {Provider}/{CandidateId}",
            User.Identity?.Name, itemId, request.ProviderName, request.ProviderItemId);
        return Ok();
    }

    /// <summary>
    /// Updates one or more metadata fields manually and sets the lock. Any field left
    /// null is preserved. Use this for typo fixes, replacing a wrong poster URL, etc.
    /// </summary>
    [HttpPatch("match/{itemId:guid}")]
    public async Task<IActionResult> ManualEdit(
        Guid itemId,
        [FromBody] ManualEditRequest request,
        [FromServices] AppDbContext db,
        CancellationToken ct)
    {
        var item = await db.MediaItems.FirstOrDefaultAsync(m => m.Id == itemId, ct);
        if (item == null) return NotFound();

        if (request.Title != null) item.Title = request.Title;
        if (request.Overview != null) item.Overview = request.Overview;
        if (request.Year.HasValue) item.Year = request.Year;
        if (request.PosterUrl != null)
        {
            item.PosterUrl = request.PosterUrl;
            item.PosterFromLocalFile = false; // explicit admin poster replaces local art (R-WI-014)
        }
        if (request.ContentRating != null) item.ContentRating = request.ContentRating;

        item.MetadataLocked = true;
        item.MetadataLockedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        _logger.LogInformation("Admin {Admin} manually edited item {ItemId}", User.Identity?.Name, itemId);
        return Ok();
    }

    /// <summary>Clears the lock; the next auto-refresh cycle will re-fetch metadata for this item.</summary>
    [HttpPost("match/{itemId:guid}/unlock")]
    public async Task<IActionResult> UnlockMatch(
        Guid itemId,
        [FromServices] AppDbContext db,
        CancellationToken ct)
    {
        var item = await db.MediaItems.FirstOrDefaultAsync(m => m.Id == itemId, ct);
        if (item == null) return NotFound();
        if (!item.MetadataLocked) return Ok(new { message = "Already unlocked." });

        item.MetadataLocked = false;
        item.MetadataLockedAt = null;
        await db.SaveChangesAsync(ct);
        _logger.LogInformation("Admin {Admin} unlocked item {ItemId}", User.Identity?.Name, itemId);
        return Ok();
    }

    /// <summary>
    /// SR-WI-036 per-item metadata refresh: clears the retry-exhausted flag and any pending
    /// retry bookkeeping, then enqueues the item on the central metadata queue (the single
    /// enrichment chokepoint, so provider rate limits and the metadata lock stay enforced in
    /// one place). Locked items return 409 — unlock first, mirroring how the queue would skip
    /// them anyway.
    /// </summary>
    [HttpPost("match/{itemId:guid}/refresh")]
    public async Task<IActionResult> RefreshMetadata(
        Guid itemId,
        [FromServices] AppDbContext db,
        [FromServices] IMetadataQueue metadataQueue,
        [FromServices] IImageCacheService imageCache,
        CancellationToken ct)
    {
        var item = await db.MediaItems.Include(m => m.Library).FirstOrDefaultAsync(m => m.Id == itemId, ct);
        if (item == null) return NotFound();

        if (item.MetadataLocked)
            return Conflict(new { message = "Metadata is locked for this item. Unlock it first to refresh." });

        item.IsRetryExhausted = false;
        var pendingRetries = await db.MetadataRetries.Where(r => r.MediaItemId == itemId).ToListAsync(ct);
        db.MetadataRetries.RemoveRange(pendingRetries);
        await db.SaveChangesAsync(ct);

        // SR-WI-037: drop this item's cached provider artwork so the refresh re-downloads
        // current images (CacheImageAsync otherwise returns any existing file forever).
        // Local-sidecar (*_local) copies are retained by design; for series-level art the
        // caller passes the series id, so an episode id here simply matches nothing.
        await imageCache.InvalidateCachedImagesAsync(item.Id);

        var libType = item.Library?.Type ?? MediaTypeLibraryMap.ForMediaType(item.Type);
        await metadataQueue.EnqueueMetadataRefreshAsync(item.Id, libType, refreshImages: true);

        _logger.LogInformation("Admin {Admin} queued a metadata refresh for item {ItemId}", User.Identity?.Name, itemId);
        return Ok(new { message = "Refresh queued." });
    }
}

public class RetryFileRequest
{
    public string Path { get; set; } = string.Empty;
}

/// <summary>Optional display name for create/rename backup endpoints.</summary>
public class BackupNameRequest
{
    public string? Name { get; set; }
}

public record SearchMatchRequest(string Query, int? Year);
public record ApplyMatchRequest(string ProviderName, string ProviderItemId);
public record ManualEditRequest(string? Title, string? Overview, int? Year, string? PosterUrl, string? ContentRating);

