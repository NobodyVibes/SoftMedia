using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SoftMedia.Server.Services.Abstractions;
using SoftMedia.Server.Services.Media;

namespace SoftMedia.Server.Controllers;

/// <summary>
/// Serves pre-generated trickplay sprite sheets + manifest for the scrubber preview
/// (P2-WI-001). Auth: class-level [Authorize] plus JwtBearerEvents lifts ?token= for
/// /api/v1/... media paths, so the player's plain &lt;img&gt;/fetch with a query token works.
/// </summary>
[Authorize]
[ApiController]
[Route("api/v1/trickplay")]
public class TrickplayController : ControllerBase
{
    private readonly ITrickplayService _trickplay;
    private readonly IMediaRepository _mediaRepository;

    public TrickplayController(ITrickplayService trickplay, IMediaRepository mediaRepository)
    {
        _trickplay = trickplay;
        _mediaRepository = mediaRepository;
    }

    // Audit L3: trickplay sheets are derived from a media item, so the same per-user
    // library ACL + content-rating ceiling that gates streaming must gate the scrubber
    // preview. GetByIdWithLibraryAsync applies both and returns null when denied — an
    // ACL-only DB check that (correctly) does NOT require the source video to still
    // exist, since the sprite sheets are independently cached.
    private async Task<bool> CanAccessAsync(Guid id)
        => await _mediaRepository.GetByIdWithLibraryAsync(id) is not null;

    [HttpGet("{id:guid}/manifest.json")]
    public async Task<IActionResult> GetManifest(Guid id)
    {
        if (!await CanAccessAsync(id)) return NotFound();
        var path = _trickplay.GetManifestPath(id);
        if (path == null) return NotFound();
        return PhysicalFile(path, "application/json");
    }

    [HttpGet("{id:guid}/{sheetFile}")]
    public async Task<IActionResult> GetSheet(Guid id, string sheetFile)
    {
        if (!await CanAccessAsync(id)) return NotFound();
        var path = _trickplay.GetSheetPath(id, sheetFile);
        if (path == null) return NotFound();
        // Immutable: sheet content for an item never changes once generated.
        Response.Headers.CacheControl = "public, max-age=31536000, immutable";
        return PhysicalFile(path, "image/jpeg");
    }
}
