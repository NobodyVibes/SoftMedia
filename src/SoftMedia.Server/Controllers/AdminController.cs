using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SoftMedia.Server.Models;
using SoftMedia.Server.Services;

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

    public AdminController(LibraryWatcher libraryWatcher, ILogger<AdminController> logger)
    {
        _libraryWatcher = libraryWatcher;
        _logger = logger;
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
}

public class RetryFileRequest
{
    public string Path { get; set; } = string.Empty;
}
