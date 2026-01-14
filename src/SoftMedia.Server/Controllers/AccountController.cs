using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SoftMedia.Server.Data;
using System.Security.Claims;

namespace SoftMedia.Server.Controllers;

/// <summary>
/// Controller for self-service account management.
/// </summary>
[ApiController]
[Route("api/v1/[controller]")]
[Authorize]
public class AccountController : ControllerBase
{
    private readonly AppDbContext _context;

    public AccountController(AppDbContext context)
    {
        _context = context;
    }

    /// <summary>
    /// Soft-deletes the current user's account.
    /// </summary>
    [HttpDelete]
    public async Task<IActionResult> DeleteAccount()
    {
        var userId = GetCurrentUserId();
        if (userId == null)
        {
            return Unauthorized();
        }

        var user = await _context.Users.FindAsync(userId.Value);
        if (user == null)
        {
            return NotFound("User not found.");
        }

        // Prevent admin from deleting themselves if they're the last admin
        if (user.Role == Models.UserRole.Admin)
        {
            var adminCount = await _context.Users.CountAsync(u => u.Role == Models.UserRole.Admin && !u.IsDeleted);
            if (adminCount <= 1)
            {
                return BadRequest("Cannot delete the last admin account. Promote another user to admin first.");
            }
        }

        // Soft delete - same logic as admin delete in UsersController
        user.IsDeleted = true;
        user.DeletedAt = DateTime.UtcNow;
        
        // Rename user to free up the username for future use
        user.Username = $"{user.Username}_deleted_{Guid.NewGuid().ToString().Substring(0, 8)}";

        // Update invite records to show user was deleted
        var usedInvites = await _context.Invites.Where(i => i.UsedById == userId.Value).ToListAsync();
        foreach (var invite in usedInvites)
        {
            if (!string.IsNullOrEmpty(invite.UsedByUsername) && !invite.UsedByUsername.Contains("(Deleted)"))
            {
                invite.UsedByUsername = $"{invite.UsedByUsername} (Deleted)";
            }
        }

        await _context.SaveChangesAsync();

        return Ok(new { message = "Account deleted successfully." });
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
