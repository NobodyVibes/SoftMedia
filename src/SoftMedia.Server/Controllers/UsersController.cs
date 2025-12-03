using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SoftMedia.Server.Data;
using SoftMedia.Server.DTOs;
using SoftMedia.Server.Models;
using SoftMedia.Server.Services;
using System.Security.Claims;

namespace SoftMedia.Server.Controllers;

[Route("api/v1/[controller]")]
[ApiController]
[Authorize(Roles = "Admin")]
public class UsersController : ControllerBase
{
    private readonly AppDbContext _context;
    private readonly IPasswordHasher _passwordHasher;

    public UsersController(AppDbContext context, IPasswordHasher passwordHasher)
    {
        _context = context;
        _passwordHasher = passwordHasher;
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
                u.IsBanned,
                u.IsApproved,
                u.IsRejected,
                string.IsNullOrEmpty(u.ContentRatings) 
                    ? new Dictionary<string, string>() 
                    : System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(u.ContentRatings, (System.Text.Json.JsonSerializerOptions?)null) ?? new Dictionary<string, string>()
            ))
            .ToListAsync();

        return Ok(users);
    }

    [HttpPost]
    public async Task<IActionResult> CreateUser(CreateUserRequest request)
    {
        if (await _context.Users.AnyAsync(u => u.Username == request.Username))
        {
            return BadRequest("Username already exists.");
        }

        if (!Enum.TryParse<UserRole>(request.Role, out var role))
        {
            return BadRequest("Invalid role.");
        }

        var user = new User
        {
            Username = request.Username,
            PasswordHash = _passwordHasher.HashPassword(request.Password),
            Role = role,
            IsApproved = true, // Admin created users are auto-approved
            ContentRatings = "{}"
        };

        _context.Users.Add(user);
        await _context.SaveChangesAsync();

        return Ok(new UserDto(
            user.Id,
            user.Username,
            user.Role,
            user.MaxRating,
            user.CreatedAt,
            user.IsBanned,
            user.IsApproved,
            user.IsRejected,
            new Dictionary<string, string>()
        ));
    }

    [HttpPut("{id}/ratings")]
    public async Task<IActionResult> UpdateUserRatings(Guid id, UpdateUserRatingsRequest request)
    {
        var user = await _context.Users.FindAsync(id);
        if (user == null)
        {
            return NotFound("User not found.");
        }

        user.ContentRatings = System.Text.Json.JsonSerializer.Serialize(request.ContentRatings);
        await _context.SaveChangesAsync();

        return Ok();
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
        var currentUserId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
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
        var currentUserId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
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

    [HttpPut("{id}/approve")]
    public async Task<IActionResult> ApproveUser(Guid id, ApproveUserRequest request)
    {
        var user = await _context.Users.FindAsync(id);
        if (user == null)
        {
            return NotFound("User not found.");
        }

        user.IsApproved = request.IsApproved;
        await _context.SaveChangesAsync();

        return Ok();
    }

    [HttpPut("{id}/deny")]
    public async Task<IActionResult> DenyUser(Guid id)
    {
        var user = await _context.Users.FindAsync(id);
        if (user == null)
        {
            return NotFound("User not found.");
        }

        user.IsRejected = true;
        user.IsApproved = false; // Ensure they are not approved
        await _context.SaveChangesAsync();

        return Ok();
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> DeleteUser(Guid id)
    {
        var user = await _context.Users.FindAsync(id);
        if (user == null)
        {
            return NotFound("User not found.");
        }

        // Get current user ID from claims
        var currentUserId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (currentUserId == null)
        {
            return Unauthorized();
        }

        // Prevent self-deletion
        if (user.Id.ToString() == currentUserId)
        {
            return BadRequest("Cannot delete yourself.");
        }

        _context.Users.Remove(user);
        await _context.SaveChangesAsync();

        return Ok();
    }
}
