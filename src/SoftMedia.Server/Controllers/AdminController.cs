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
    private readonly MetadataRefreshService _metadataRefreshService;

    public AdminController(
        LibraryWatcher libraryWatcher,
        ILogger<AdminController> logger,
        IEnumerable<IMetadataProvider> providers,
        IRecommendationService recommendationService,
        IBackupService backupService,
        IScheduledTaskRegistry taskRegistry,
        MetadataRefreshService metadataRefreshService)
    {
        _libraryWatcher = libraryWatcher;
        _logger = logger;
        _providers = providers;
        _recommendationService = recommendationService;
        _backupService = backupService;
        _taskRegistry = taskRegistry;
        _metadataRefreshService = metadataRefreshService;
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
        await _recommendationService.UpdateHeroCacheAsync();
        return Ok(new { success = true, message = "Hero cache refresh completed" });
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

    /// <summary>Creates a backup on the server and returns its metadata.</summary>
    [HttpPost("backup")]
    public async Task<IActionResult> CreateBackup(CancellationToken ct)
    {
        var info = await _backupService.CreateBackupAsync(ct);
        _logger.LogInformation("Admin {User} created backup {Id}", User.Identity?.Name, info.Id);
        return Ok(info);
    }

    /// <summary>Lists backups on disk, newest first.</summary>
    [HttpGet("backup")]
    public async Task<IActionResult> ListBackups(CancellationToken ct)
        => Ok(await _backupService.ListBackupsAsync(ct));

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

    // --- Scheduled tasks (P1-WI-005) ---

    /// <summary>Lists all background tasks with their last-run telemetry.</summary>
    [HttpGet("tasks")]
    public IActionResult GetTasks() => Ok(_taskRegistry.GetAll());

    /// <summary>
    /// Manually triggers a task that supports it. v1 supports the metadata refresh;
    /// other tasks return 400. Result is reflected on the next GET /tasks.
    /// </summary>
    [HttpPost("tasks/{name}/trigger")]
    public IActionResult TriggerTask(string name)
    {
        if (name == ScheduledTaskNames.MetadataRefresh)
        {
            _metadataRefreshService.TriggerRefreshNow();
            _logger.LogInformation("Admin {User} manually triggered {Task}", User.Identity?.Name, name);
            return Accepted(new { message = $"{name} triggered." });
        }
        return BadRequest($"Task '{name}' does not support manual triggering.");
    }

    /// <summary>
    /// Admin recovery: disables 2FA for a user who is locked out (lost device + recovery
    /// codes). The only out-of-band reset path — there is intentionally no self-service
    /// bypass (P2-WI-005).
    /// </summary>
    [HttpPost("users/{userId:guid}/disable-2fa")]
    public async Task<IActionResult> DisableUserTwoFactor(Guid userId, [FromServices] AppDbContext db)
    {
        var totp = await db.UserTotps.FirstOrDefaultAsync(t => t.UserId == userId);
        if (totp == null) return NotFound("User has no 2FA enrollment.");
        db.UserTotps.Remove(totp);
        await db.SaveChangesAsync();
        _logger.LogWarning("Admin {Admin} disabled 2FA for user {UserId}", User.Identity?.Name, userId);
        return Ok();
    }
}

public class RetryFileRequest
{
    public string Path { get; set; } = string.Empty;
}

