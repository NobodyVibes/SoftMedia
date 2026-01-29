using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SoftMedia.Server.Services.Identity;
using SoftMedia.Server.Services.Infrastructure;
using SoftMedia.Server.Services.Media;
using SoftMedia.Server.Services.Scanning;
using SoftMedia.Server.Services.Transcoding;

namespace SoftMedia.Server.Controllers;

[ApiController]
[Route("api/v1/notifications")]
[Authorize(Roles = "Admin")]
public class NotificationsController : ControllerBase
{
    private readonly INotificationService _notificationService;

    public NotificationsController(INotificationService notificationService)
    {
        _notificationService = notificationService;
    }

    /// <summary>
    /// Get all active (non-dismissed) system notifications.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> GetActive()
    {
        var notifications = await _notificationService.GetActiveAsync();
        return Ok(notifications);
    }

    /// <summary>
    /// Dismiss a notification.
    /// </summary>
    [HttpPost("{id}/dismiss")]
    public async Task<IActionResult> Dismiss(Guid id)
    {
        var username = User.Identity?.Name ?? "Unknown";
        await _notificationService.DismissAsync(id, username);
        return NoContent();
    }
}
