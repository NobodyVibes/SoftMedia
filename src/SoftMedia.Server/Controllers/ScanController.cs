using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using SoftMedia.Server.Data;
using SoftMedia.Server.Extensions;
using SoftMedia.Server.Services.Identity;
using SoftMedia.Server.Services.Scanning;
using SoftMedia.Server.Services.Security.LibraryAccess;

namespace SoftMedia.Server.Controllers;

/// <summary>
/// R-WI-019 — inbound scan trigger for Sonarr/Radarr ("Connect → Webhook").
/// Gated by the <see cref="ScopePolicies.WriteLibrary"/> scope so the credential
/// living in an *arr config is least-privilege: it can trigger scans and nothing
/// else. Authorization layers (review HIGH):
/// 1. the scope policy admits API tokens holding write:library — and, by the
///    policy model's design, every full session; so
/// 2. the controller additionally requires full SESSIONS to be admin (scanning
///    was admin-only before this endpoint existed; a plain user session gains
///    nothing here), while scoped tokens act for their owning user; and
/// 3. every branch sees only the caller's ACL-visible libraries — hidden library
///    names/ids never appear in responses, and a path inside a hidden library
///    answers exactly like a path outside every library (anti-probe).
/// </summary>
[ApiController]
[Authorize(Policy = ScopePolicies.WriteLibrary)]
[EnableRateLimiting(ServiceCollectionExtensions.WebhookRateLimitPolicy)]
[Route("api/v1/scan")]
public class ScanController : ControllerBase
{
    private readonly AppDbContext _context;
    private readonly ILibraryScanQueueService _scanQueue;
    private readonly IUserLibraryAccessProvider _libraryAccess;
    private readonly ILogger<ScanController> _logger;

    public ScanController(
        AppDbContext context,
        ILibraryScanQueueService scanQueue,
        IUserLibraryAccessProvider libraryAccess,
        ILogger<ScanController> logger)
    {
        _context = context;
        _scanQueue = scanQueue;
        _libraryAccess = libraryAccess;
        _logger = logger;
    }

    public record ScanResponse(Guid LibraryId, string LibraryName, Guid JobId, bool AlreadyQueued);

    [HttpPost]
    public async Task<IActionResult> TriggerScan([FromBody] JsonElement? body)
    {
        // Sessions must be admin (see class doc); API-token principals carry scope
        // claims and were already vetted for write:library by the policy.
        var isApiToken = User.HasClaim(c => c.Type == ApiTokenAuthenticationHandler.ScopeClaimType);
        if (!isApiToken && !User.IsInRole("Admin"))
        {
            return Forbid();
        }

        // The *arr "Test" connection button posts eventType=Test with no import —
        // answer success without churning the scan queue.
        if (string.Equals(GetString(body, "eventType"), "Test", StringComparison.OrdinalIgnoreCase))
        {
            return Ok(new { message = "SoftMedia scan webhook is configured correctly." });
        }

        // Real *arr payloads nest the imported path (review MED: there is no
        // top-level `path` in their webhook schema) — probe the known shapes,
        // preferring the most specific. A plain {"path": …} body also works for
        // hand-rolled automations.
        var path = GetString(body, "path")
                   ?? GetString(body, "episodeFile", "path")
                   ?? GetString(body, "movieFile", "path")
                   ?? GetString(body, "movie", "folderPath")
                   ?? GetString(body, "movie", "path")
                   ?? GetString(body, "series", "path");

        var access = await _libraryAccess.GetCurrentAsync();
        var libraries = await _context.Libraries.AsNoTracking()
            .ApplyLibraryAccessFilter(access)
            .ToListAsync();

        // No path → scan the caller's visible libraries (a generic "something was
        // imported" ping).
        if (string.IsNullOrWhiteSpace(path))
        {
            var jobs = new List<ScanResponse>();
            foreach (var lib in libraries)
            {
                var queued = _scanQueue.IsLibraryInQueue(lib.Id); // best-effort flag; the queue's own dedup is atomic
                var job = _scanQueue.EnqueueScan(lib.Id, lib.Name);
                jobs.Add(new ScanResponse(lib.Id, lib.Name, job.Id, queued));
            }
            _logger.LogInformation("Webhook scan trigger (no path): {Count} libraries enqueued", jobs.Count);
            return Accepted(jobs);
        }

        // Path present → it must resolve INSIDE a visible library root (path-jail:
        // no scan triggers for arbitrary filesystem locations, no oracle for what
        // exists outside the caller's roots). Note: the check is lexical
        // (GetFullPath) — a path through a symlinked alias of a root will 404;
        // configure *arr with the same path the library uses.
        string fullPath;
        try
        {
            fullPath = System.IO.Path.GetFullPath(path);
        }
        catch (Exception)
        {
            // SR-WI-061: RFC 7807 body (was { error }).
            return Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "Invalid path",
                detail: "Invalid path.");
        }

        var owner = libraries.FirstOrDefault(lib => lib.Paths.Any(root => IsUnderRoot(fullPath, root)));
        if (owner == null)
        {
            // Deliberately vague: don't reveal whether the path exists, which roots
            // are configured, or whether it belongs to a library hidden by ACL.
            _logger.LogWarning("Webhook scan trigger rejected: path outside the caller's library roots");
            // SR-WI-061: RFC 7807 body (was { error }).
            return Problem(
                statusCode: StatusCodes.Status404NotFound,
                title: "Not found",
                detail: "Path is not inside any configured library.");
        }

        var alreadyQueued = _scanQueue.IsLibraryInQueue(owner.Id); // best-effort flag
        var scanJob = _scanQueue.EnqueueScan(owner.Id, owner.Name);
        _logger.LogInformation(
            "Webhook scan trigger: library {Library} ({Id}) enqueued (alreadyQueued={AlreadyQueued})",
            owner.Name, owner.Id, alreadyQueued);
        return Accepted(new ScanResponse(owner.Id, owner.Name, scanJob.Id, alreadyQueued));
    }

    /// <summary>Case-insensitive property lookup through nested objects (the *arr
    /// tools serialize camelCase; hand-rolled callers may not).</summary>
    private static string? GetString(JsonElement? body, params string[] pathSegments)
    {
        if (body is not { ValueKind: JsonValueKind.Object } current) return null;
        foreach (var segment in pathSegments)
        {
            JsonElement? next = null;
            foreach (var prop in current.EnumerateObject())
            {
                if (string.Equals(prop.Name, segment, StringComparison.OrdinalIgnoreCase))
                {
                    next = prop.Value;
                    break;
                }
            }
            if (next is not { } n) return null;
            if (n.ValueKind == JsonValueKind.Object) { current = n; continue; }
            return n.ValueKind == JsonValueKind.String ? n.GetString() : null;
        }
        return null;
    }

    /// <summary>
    /// Case-insensitive (media lives on Windows/SMB mounts), separator-normalised
    /// prefix check with a boundary guard so "/media/tvx" is not "under" "/media/tv".
    /// </summary>
    private static bool IsUnderRoot(string fullPath, string root)
    {
        if (string.IsNullOrWhiteSpace(root)) return false;
        string canonicalRoot;
        try
        {
            canonicalRoot = System.IO.Path.GetFullPath(root);
        }
        catch (Exception)
        {
            return false;
        }

        var normalizedPath = fullPath.Replace('/', System.IO.Path.DirectorySeparatorChar)
            .TrimEnd(System.IO.Path.DirectorySeparatorChar);
        var normalizedRoot = canonicalRoot.Replace('/', System.IO.Path.DirectorySeparatorChar)
            .TrimEnd(System.IO.Path.DirectorySeparatorChar);

        if (normalizedPath.Equals(normalizedRoot, StringComparison.OrdinalIgnoreCase)) return true;
        return normalizedPath.StartsWith(normalizedRoot + System.IO.Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);
    }
}
