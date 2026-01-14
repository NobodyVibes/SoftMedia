using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using SoftMedia.Server.Data;
using SoftMedia.Server.DTOs;
using SoftMedia.Server.Models;
using SoftMedia.Server.Services;

namespace SoftMedia.Server.Controllers;

[Route("api/v1/[controller]")]
[ApiController]
public class AuthController : ControllerBase
{
    private readonly AppDbContext _context;
    private readonly IPasswordHasher _passwordHasher;
    private readonly ITokenService _tokenService;
    private readonly ISettingsService _settingsService;
    private readonly IUserPreferencesService _userPreferencesService;

    public AuthController(AppDbContext context, IPasswordHasher passwordHasher, ITokenService tokenService, ISettingsService settingsService, IUserPreferencesService userPreferencesService)
    {
        _context = context;
        _passwordHasher = passwordHasher;
        _tokenService = tokenService;
        _settingsService = settingsService;
        _userPreferencesService = userPreferencesService;
    }

    [HttpPost("signup")]
    public async Task<ActionResult<AuthResponse>> Signup(SignupRequest request)
    {
        var signupSetting = await _settingsService.GetSettingAsync("AllowUserSignup", "Disabled");
        
        // First user setup is always allowed
        if (!await _context.Users.AnyAsync())
        {
            // Proceed to creation
        }
        else if (signupSetting == "Disabled")
        {
            return Forbid("Public signup is disabled.");
        }
        else if (signupSetting == "InviteOnly" && string.IsNullOrEmpty(request.InviteCode))
        {
            return BadRequest("Invite code is required.");
        }

        if (await _context.Users.AnyAsync(u => u.Username == request.Username))
        {
            return BadRequest("Username already exists.");
        }

        // Validate invite code if provided
        if (!string.IsNullOrEmpty(request.InviteCode))
        {
            var invite = await _context.Invites
                .Include(i => i.UsedBy)
                .FirstOrDefaultAsync(i => i.Code == request.InviteCode);

            if (invite == null)
            {
                return BadRequest("Invalid invite code.");
            }

            if (invite.IsRevoked)
            {
                return BadRequest("This invite has been revoked.");
            }

            if (invite.UsedAt != null)
            {
                return BadRequest("This invite has already been used.");
            }

            if (invite.ExpiresAt != null && invite.ExpiresAt < DateTime.UtcNow)
            {
                return BadRequest("This invite has expired.");
            }
        }
        // If there are existing users and no invite code provided, check if invites are required
        else if (await _context.Users.AnyAsync())
        {
            // For now, we'll allow signup without invite. This can be controlled by a setting later.
            // TODO: Add RequireInviteForSignup setting
        }

        var user = new User
        {
            Username = request.Username,
            PasswordHash = _passwordHasher.HashPassword(request.Password),
            Role = UserRole.User, // Default role
            CreatedAt = DateTime.UtcNow,
            FirstName = request.FirstName,
            LastName = request.LastName
        };

        // First user becomes Admin and is Approved
        if (!await _context.Users.AnyAsync())
        {
            user.Role = UserRole.Admin;
            user.IsApproved = true;
        }

        // Auto-approve if invite code was used
        if (!string.IsNullOrEmpty(request.InviteCode))
        {
             user.IsApproved = true;
        }

        _context.Users.Add(user);

        // Mark invite as used if one was provided
        if (!string.IsNullOrEmpty(request.InviteCode))
        {
            var invite = await _context.Invites.FirstOrDefaultAsync(i => i.Code == request.InviteCode);
            if (invite != null)
            {
                invite.UsedAt = DateTime.UtcNow;
                invite.UsedById = user.Id;
                invite.UsedByUsername = user.Username;
            }
        }

        await _context.SaveChangesAsync();

        // Initialize default preferences for the new user
        await _userPreferencesService.InitializeDefaultsAsync(user.Id);

        var accessToken = _tokenService.GenerateAccessToken(user);
        var refreshToken = _tokenService.GenerateRefreshToken();

        SetRefreshToken(refreshToken);

        return Ok(new AuthResponse(accessToken, new UserDto(user.Id, user.Username, user.Role, user.MaxRating, user.CreatedAt, user.IsBanned, user.IsApproved, user.IsRejected, new Dictionary<string, string>(), user.FirstName, user.LastName, user.CreatedByAdmin, request.InviteCode, false)));
    }

    [HttpPost("login")]
    public async Task<ActionResult<AuthResponse>> Login(LoginRequest request)
    {
        var user = await _context.Users.FirstOrDefaultAsync(u => u.Username == request.Username);
        if (user == null || !_passwordHasher.VerifyPassword(request.Password, user.PasswordHash))
        {
            return Unauthorized("Invalid username or password.");
        }

        // Check if user is banned
        if (user.IsBanned)
        {
            return Unauthorized("This account has been banned.");
        }

        // Check if user is deleted
        if (user.IsDeleted)
        {
            return Unauthorized("Invalid username or password.");
        }

        // Check if user is approved
        if (!user.IsApproved)
        {
            return Unauthorized("Account pending approval.");
        }

        var accessToken = _tokenService.GenerateAccessToken(user);
        var refreshToken = _tokenService.GenerateRefreshToken();

        SetRefreshToken(refreshToken);

        var usedInviteCode = await _context.Invites.Where(i => i.UsedById == user.Id).Select(i => i.Code).FirstOrDefaultAsync();

        return Ok(new AuthResponse(accessToken, new UserDto(user.Id, user.Username, user.Role, user.MaxRating, user.CreatedAt, user.IsBanned, user.IsApproved, user.IsRejected, string.IsNullOrEmpty(user.ContentRatings) ? new Dictionary<string, string>() : System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(user.ContentRatings, (System.Text.Json.JsonSerializerOptions?)null) ?? new Dictionary<string, string>(), user.FirstName, user.LastName, user.CreatedByAdmin, usedInviteCode, user.MustChangePassword)));
    }

    [Authorize]
    [HttpPost("change-password")]
    public async Task<IActionResult> ChangePassword(ChangePasswordRequest request)
    {
        // Get current user ID from claims
        var userIdString = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        if (userIdString == null || !Guid.TryParse(userIdString, out var userId))
        {
            return Unauthorized();
        }

        var user = await _context.Users.FindAsync(userId);
        if (user == null)
        {
            return NotFound("User not found.");
        }

        if (!_passwordHasher.VerifyPassword(request.OldPassword, user.PasswordHash))
        {
            return BadRequest("Invalid old password.");
        }

        user.PasswordHash = _passwordHasher.HashPassword(request.NewPassword);
        user.MustChangePassword = false; // Reset the flag

        await _context.SaveChangesAsync();

        return Ok("Password changed successfully.");
    }

    [HttpPost("refresh-token")]
    public Task<ActionResult<AuthResponse>> Refresh()
    {
        var refreshToken = Request.Cookies["refreshToken"];
        if (string.IsNullOrEmpty(refreshToken))
        {
            return Task.FromResult<ActionResult<AuthResponse>>(Unauthorized("No refresh token provided."));
        }

        // TODO: Implement proper Refresh Token persistence and rotation.
        // For now, we rely on the longer access token expiry (24h) and this endpoint exists 
        // to prevent 404 errors on the frontend, signalling re-login is required if access token DOES expire.
        
        return Task.FromResult<ActionResult<AuthResponse>>(Unauthorized("Refresh token expired or invalid. Please login again."));
    }

    [HttpPost("logout")]
    public IActionResult Logout()
    {
        Response.Cookies.Delete("refreshToken");
        return Ok("Logged out.");
    }

    private void SetRefreshToken(string refreshToken)
    {
        var cookieOptions = new CookieOptions
        {
            HttpOnly = true,
            Expires = DateTime.UtcNow.AddDays(7),
            SameSite = SameSiteMode.Strict,
            Secure = true
        };
        Response.Cookies.Append("refreshToken", refreshToken, cookieOptions);
    }
}
