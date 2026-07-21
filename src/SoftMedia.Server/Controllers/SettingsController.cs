using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SoftMedia.Server.Models;
using SoftMedia.Server.Services.Identity;
using SoftMedia.Server.Services.Infrastructure;
using SoftMedia.Server.Services.Media;
using SoftMedia.Server.Services.Scanning;
using SoftMedia.Server.Services.Transcoding;

namespace SoftMedia.Server.Controllers;

[ApiController]
[Route("api/v1/[controller]")]
[Authorize(Roles = "Admin")]
public class SettingsController : ControllerBase
{
    private readonly ISettingsService _settingsService;
    private readonly MetadataRefreshService _metadataRefreshService;
    private readonly IRuntimeLogLevel _runtimeLogLevel;

    public SettingsController(
        ISettingsService settingsService,
        MetadataRefreshService metadataRefreshService,
        IRuntimeLogLevel runtimeLogLevel)
    {
        _settingsService = settingsService;
        _metadataRefreshService = metadataRefreshService;
        _runtimeLogLevel = runtimeLogLevel;
    }

    [HttpGet]
    public async Task<ActionResult<List<AppSetting>>> GetSettings()
    {
        return await _settingsService.GetAllSettingsAsync();
    }

    [HttpPut]
    public async Task<IActionResult> UpdateSettings([FromBody] List<AppSetting> settings)
    {
        await _settingsService.UpdateSettingsAsync(settings);

        // NR-WI-011: log verbosity applies immediately — this controller is the only
        // write path for settings, so the hook lives here rather than in the service.
        var logLevel = settings.FirstOrDefault(s => s.Key == "LogLevel")?.Value;
        if (!string.IsNullOrEmpty(logLevel))
        {
            _runtimeLogLevel.Apply(logLevel);
        }

        return Ok();
    }
    
    /// <summary>
    /// Manually trigger metadata refresh for ongoing (Running) TV series.
    /// </summary>
    /// <remarks>
    /// Retained for backwards compatibility. Prefer the generic task-trigger endpoint
    /// POST /api/v1/admin/tasks/{name}/trigger (P1-WI-005), which routes to the same
    /// MetadataRefreshService.TriggerRefreshNow().
    /// </remarks>
    [Obsolete("Use POST /api/v1/admin/tasks/{name}/trigger instead.")]
    [HttpPost("refresh-metadata")]
    public IActionResult TriggerMetadataRefresh()
    {
        _metadataRefreshService.TriggerRefreshNow();
        return Ok(new { message = "Metadata refresh triggered" });
    }
}
