using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SoftMedia.Server.Data;
using SoftMedia.Server.Services.Abstractions;
using System.IO;

namespace SoftMedia.Server.Controllers;

[ApiController]
[Authorize]
[Route("api/v1/[controller]")]
public class AudioController : ControllerBase
{
    private readonly AppDbContext _context;
    private readonly IStreamSecurityService _streamSecurity;
    private readonly IWebHostEnvironment _env;
    private readonly ILogger<AudioController> _logger;

    public AudioController(
        AppDbContext context,
        IStreamSecurityService streamSecurity,
        IWebHostEnvironment env,
        ILogger<AudioController> logger)
    {
        _context = context;
        _streamSecurity = streamSecurity;
        _env = env;
        _logger = logger;
    }

    [HttpGet("{id}/cover")]
    [ResponseCache(Duration = 86400, Location = ResponseCacheLocation.Client)] // Cache for 1 day
    public async Task<IActionResult> GetCoverArt(Guid id)
    {
        // Include Library so the fallback path can be validated against its jail.
        var item = await _context.MediaItems
            .Include(m => m.Library)
            .FirstOrDefaultAsync(m => m.Id == id);
        if (item == null)
            return NotFound();

        // 1. Try cached cover art path — jailed to wwwroot via StreamSecurityService.
        if (!string.IsNullOrEmpty(item.CoverArtPath))
        {
            var wwwroot = Path.GetFullPath(_env.WebRootPath ??
                Path.Combine(Directory.GetCurrentDirectory(), "wwwroot"));
            var candidate = Path.Combine(wwwroot, item.CoverArtPath.TrimStart('/', '\\'));

            if (!_streamSecurity.IsPathAuthorized(candidate, new[] { wwwroot }))
            {
                // Stored CoverArtPath escaped wwwroot. Log and fall through to
                // the embedded-tag extraction path so a broken metadata row
                // doesn't break the whole endpoint.
                _logger.LogWarning(
                    "Cover art path outside wwwroot rejected for item {Id}: {Path}",
                    id, item.CoverArtPath);
            }
            else if (System.IO.File.Exists(candidate))
            {
                var mimeType = Path.GetExtension(candidate).ToLowerInvariant() switch
                {
                    ".jpg" or ".jpeg" => "image/jpeg",
                    ".png" => "image/png",
                    ".webp" => "image/webp",
                    _ => "image/jpeg"
                };
                var stream = System.IO.File.OpenRead(candidate);
                return File(stream, mimeType);
            }
        }

        // 2. Fallback: extract embedded cover art from the audio file via TagLib.
        // The audio file path must pass the library-jail check (and Wave C
        // per-user ACL) before we open it.
        var access = await _streamSecurity.ValidateMediaAccessAsync(item);
        if (access != MediaAccessResult.Allowed)
        {
            return NotFound();
        }

        try
        {
            using var tfile = TagLib.File.Create(item.Path);
            if (tfile.Tag.Pictures.Length > 0)
            {
                var pic = tfile.Tag.Pictures[0];
                return File(pic.Data.Data, pic.MimeType);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to extract cover art for {Id}", id);
        }

        return NotFound();
    }
}
