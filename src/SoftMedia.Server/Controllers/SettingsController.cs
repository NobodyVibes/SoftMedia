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

    public SettingsController(ISettingsService settingsService, MetadataRefreshService metadataRefreshService)
    {
        _settingsService = settingsService;
        _metadataRefreshService = metadataRefreshService;
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
        return Ok();
    }
    
    /// <summary>
    /// Manually trigger metadata refresh for ongoing (Running) TV series.
    /// </summary>
    [HttpPost("refresh-metadata")]
    public IActionResult TriggerMetadataRefresh()
    {
        _metadataRefreshService.TriggerRefreshNow();
        return Ok(new { message = "Metadata refresh triggered" });
    }
}
