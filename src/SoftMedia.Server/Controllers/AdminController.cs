using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SoftMedia.Server.Models;
using SoftMedia.Server.Services.Identity;
using SoftMedia.Server.Services.Infrastructure;
using SoftMedia.Server.Services.Media;
using SoftMedia.Server.Services.Scanning;
using SoftMedia.Server.Services.Transcoding;
using SoftMedia.Server.Services.Metadata;

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

    public AdminController(
        LibraryWatcher libraryWatcher, 
        ILogger<AdminController> logger,
        IEnumerable<IMetadataProvider> providers)
    {
        _libraryWatcher = libraryWatcher;
        _logger = logger;
        _providers = providers;
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
}

public class RetryFileRequest
{
    public string Path { get; set; } = string.Empty;
}

