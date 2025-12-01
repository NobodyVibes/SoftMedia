using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SoftMedia.Server.Data;
using SoftMedia.Server.DTOs;
using SoftMedia.Server.Models;
using System.Security.Claims;

namespace SoftMedia.Server.Controllers;

[Route("api/v1/[controller]")]
[ApiController]
[Authorize(Roles = "Admin")]
public class UsersController : ControllerBase
{
    private readonly AppDbContext _context;
    private readonly ILogger<UsersController> _logger;

    public UsersController(AppDbContext context, ILogger<UsersController> logger)
    {
        _context = context;
        _logger = logger;
    }

    [HttpGet]
    public async Task<ActionResult<IEnumerable<UserDto>>> GetUsers()
    {
        var users = await _context.Users
            .Select(u => new UserDto(
                u.Id,
                u.Username,
                u.Role,
                u.MaxRating,
                u.CreatedAt,
                u.IsBanned
            ))
            .ToListAsync();

        return Ok(users);
    }

    [HttpPut("{id}/role")]
    public async Task<IActionResult> UpdateUserRole(Guid id, UpdateUserRoleRequest request)
    {
        var user = await _context.Users.FindAsync(id);
        if (user == null)
        {
            return NotFound("User not found.");
        }

        // Get current user ID from claims
        var currentUserId = GetCurrentUserId();
        if (currentUserId == null)
        {
            return Unauthorized();
        }

        // Prevent self-demotion if you're the last admin
        if (user.Id.ToString() == currentUserId && request.Role == "User")
        {
            var adminCount = await _context.Users.CountAsync(u => u.Role == UserRole.Admin);
            if (adminCount <= 1)
            {
                return BadRequest("Cannot demote the last admin user.");
            }
        }

        // Validate role
        if (!Enum.TryParse<UserRole>(request.Role, out var newRole))
        {
            return BadRequest("Invalid role.");
        }

        user.Role = newRole;
        await _context.SaveChangesAsync();

        return Ok();
    }

    [HttpPut("{id}/ban")]
    public async Task<IActionResult> BanUser(Guid id, BanUserRequest request)
    {
        var user = await _context.Users.FindAsync(id);
        if (user == null)
        {
            return NotFound("User not found.");
        }

        // Get current user ID from claims
        var currentUserId = GetCurrentUserId();
        if (currentUserId == null)
        {
            return Unauthorized();
        }

        // Prevent self-ban
        if (user.Id.ToString() == currentUserId)
        {
            return BadRequest("Cannot ban yourself.");
        }

        user.IsBanned = request.IsBanned;
        await _context.SaveChangesAsync();

        return Ok();
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> DeleteUser(Guid id)
    {
        _logger.LogInformation($"Attempting to delete user {id}");

        var user = await _context.Users.FindAsync(id);
        if (user == null)
        {
            _logger.LogWarning($"User {id} not found");
            return NotFound("User not found.");
        }

        // Get current user ID from claims
        var currentUserId = GetCurrentUserId();
        if (currentUserId == null)
        {
            _logger.LogWarning("Current user ID could not be determined from claims");
            foreach (var claim in User.Claims)
            {
                _logger.LogDebug($"Claim: {claim.Type} = {claim.Value}");
            }
            return Unauthorized();
        }

        // Prevent self-deletion
        if (user.Id.ToString() == currentUserId)
        {
            _logger.LogWarning($"User {currentUserId} attempted to delete themselves");
            return BadRequest("Cannot delete yourself.");
        }

        _context.Users.Remove(user);
        await _context.SaveChangesAsync();
        
        _logger.LogInformation($"User {id} deleted successfully");

        return Ok();
    }

    private string? GetCurrentUserId()
    {
        return User.FindFirst("sub")?.Value 
            ?? User.FindFirst(ClaimTypes.NameIdentifier)?.Value
            ?? User.FindFirst("http://schemas.xmlsoap.org/ws/2005/05/identity/claims/nameidentifier")?.Value;
    }
}
