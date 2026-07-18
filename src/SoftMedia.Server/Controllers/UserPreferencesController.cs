using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SoftMedia.Server.DTOs;
using SoftMedia.Server.Services.Identity;
using SoftMedia.Server.Services.Infrastructure;
using SoftMedia.Server.Services.Media;
using SoftMedia.Server.Services.Scanning;
using SoftMedia.Server.Services.Transcoding;
using System.Security.Claims;

namespace SoftMedia.Server.Controllers;

/// <summary>
/// Controller for managing the current user's preferences.
/// </summary>
[ApiController]
[Route("api/v1/[controller]")]
[Authorize]
public class UserPreferencesController : ControllerBase
{
    private readonly IUserPreferencesService _userPreferencesService;

    public UserPreferencesController(IUserPreferencesService userPreferencesService)
    {
        _userPreferencesService = userPreferencesService;
    }

    /// <summary>
    /// Gets the current user's preferences.
    /// </summary>
    [HttpGet]
    [Authorize(Policy = ScopePolicies.ReadState)] // B-18: preferences = user state
    public async Task<ActionResult<Dictionary<string, string>>> GetMyPreferences()
    {
        var userId = GetCurrentUserId();
        if (userId == null)
        {
            return Unauthorized();
        }

        var preferences = await _userPreferencesService.GetPreferencesAsync(userId.Value);
        return Ok(preferences);
    }

    /// <summary>
    /// Updates the current user's preferences.
    /// </summary>
    [HttpPut]
    [Authorize(Policy = ScopePolicies.WriteState)] // R-WI-006: read-only API tokens must not mutate
    public async Task<IActionResult> UpdateMyPreferences([FromBody] UpdatePreferencesRequest request)
    {
        var userId = GetCurrentUserId();
        if (userId == null)
        {
            return Unauthorized();
        }

        if (request.Preferences == null || request.Preferences.Count == 0)
        {
            return BadRequest("No preferences provided.");
        }

        await _userPreferencesService.SetPreferencesAsync(userId.Value, request.Preferences);
        return Ok();
    }

    private Guid? GetCurrentUserId()
    {
        var userIdString = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (userIdString == null || !Guid.TryParse(userIdString, out var userId))
        {
            return null;
        }
        return userId;
    }
}
