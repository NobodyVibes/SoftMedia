using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SoftMedia.Server.Models;
using SoftMedia.Server.Services;

namespace SoftMedia.Server.Controllers;

[ApiController]
[Route("api/v1/[controller]")]
[Authorize(Roles = "Admin")]
public class SettingsController : ControllerBase
{
    private readonly ISettingsService _settingsService;

    public SettingsController(ISettingsService settingsService)
    {
        _settingsService = settingsService;
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
}
