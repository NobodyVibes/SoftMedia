using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
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

    public TrickplayController(ITrickplayService trickplay) => _trickplay = trickplay;

    [HttpGet("{id:guid}/manifest.json")]
    public IActionResult GetManifest(Guid id)
    {
        var path = _trickplay.GetManifestPath(id);
        if (path == null) return NotFound();
        return PhysicalFile(path, "application/json");
    }

    [HttpGet("{id:guid}/{sheetFile}")]
    public IActionResult GetSheet(Guid id, string sheetFile)
    {
        var path = _trickplay.GetSheetPath(id, sheetFile);
        if (path == null) return NotFound();
        // Immutable: sheet content for an item never changes once generated.
        Response.Headers.CacheControl = "public, max-age=31536000, immutable";
        return PhysicalFile(path, "image/jpeg");
    }
}
