using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SoftMedia.Server.Data;
using SoftMedia.Server.DTOs;
using SoftMedia.Server.Extensions;
using SoftMedia.Server.Models;
using SoftMedia.Server.Services.Identity;
using System.Security.Claims;
using System.Text.Json;

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
    private readonly IApiTokenService _apiTokens;
    private readonly ITotpService _totp;
    private readonly ITrustedDeviceService _trustedDevices;
    private readonly IPasswordHasher _passwordHasher;

    public AccountController(AppDbContext context, IApiTokenService apiTokens, ITotpService totp, ITrustedDeviceService trustedDevices, IPasswordHasher passwordHasher)
    {
        _context = context;
        _apiTokens = apiTokens;
        _totp = totp;
        _trustedDevices = trustedDevices;
        _passwordHasher = passwordHasher;
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

    // --- API tokens (P1-WI-002) ---

    /// <summary>Lists the calling user's active API tokens (never the raw secret).</summary>
    [HttpGet("api-tokens")]
    public async Task<IActionResult> ListApiTokens(CancellationToken ct)
    {
        var userId = GetCurrentUserId();
        if (userId == null) return Unauthorized();

        var tokens = await _apiTokens.ListAsync(userId.Value, ct);
        var dtos = tokens.Select(t => new ApiTokenDto(
            t.Id,
            t.Label,
            JsonSerializer.Deserialize<List<string>>(t.Scopes) ?? new List<string>(),
            t.CreatedAt,
            t.LastUsedAt,
            t.LastUsedIp,
            t.ExpiresAt));
        return Ok(dtos);
    }

    /// <summary>Mints a new API token. The raw token is returned exactly once.</summary>
    [HttpPost("api-tokens")]
    public async Task<IActionResult> CreateApiToken([FromBody] CreateApiTokenRequest request, CancellationToken ct)
    {
        var userId = GetCurrentUserId();
        if (userId == null) return Unauthorized();

        try
        {
            var (raw, entity) = await _apiTokens.CreateAsync(
                userId.Value, request.Label, request.Scopes ?? new List<string>(), request.ExpiresAt, ct);
            // The raw token is shown ONCE here and never retrievable again.
            return Ok(new CreateApiTokenResponse(entity.Id, raw, entity.Label));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ex.Message);
        }
    }

    /// <summary>Revokes one of the caller's API tokens.</summary>
    [HttpDelete("api-tokens/{id:guid}")]
    public async Task<IActionResult> RevokeApiToken(Guid id, CancellationToken ct)
    {
        var userId = GetCurrentUserId();
        if (userId == null) return Unauthorized();

        return await _apiTokens.RevokeAsync(userId.Value, id, ct)
            ? Ok()
            : NotFound("Token not found.");
    }

    // --- TOTP two-factor auth (P2-WI-005) ---

    /// <summary>Reports whether 2FA is currently enabled for the caller.</summary>
    [HttpGet("totp")]
    public async Task<IActionResult> GetTotpStatus()
    {
        var userId = GetCurrentUserId();
        if (userId == null) return Unauthorized();
        var enabled = await _context.UserTotps.AnyAsync(t => t.UserId == userId.Value && t.EnabledAt != null);
        return Ok(new TotpStatusResponse(enabled));
    }

    /// <summary>
    /// Begins enrollment: generates a secret + otpauth URI (client renders the QR) and
    /// stores the encrypted secret in a not-yet-enabled row. Confirm with a code next.
    /// </summary>
    [HttpPost("totp/enroll")]
    public async Task<IActionResult> EnrollTotp()
    {
        var userId = GetCurrentUserId();
        if (userId == null) return Unauthorized();

        var user = await _context.Users.FindAsync(userId.Value);
        if (user == null) return NotFound();

        var existing = await _context.UserTotps.FirstOrDefaultAsync(t => t.UserId == userId.Value);
        if (existing?.EnabledAt != null) return BadRequest("2FA is already enabled.");

        var enrollment = _totp.CreateEnrollment(user.Username);
        var encrypted = _totp.EncryptSecret(enrollment.Secret);

        if (existing == null)
        {
            _context.UserTotps.Add(new UserTotp { UserId = userId.Value, EncryptedSecret = encrypted });
        }
        else
        {
            existing.EncryptedSecret = encrypted; // re-enroll overwrites a pending secret
        }
        await _context.SaveChangesAsync();

        return Ok(new TotpEnrollResponse(enrollment.Secret, enrollment.OtpAuthUri));
    }

    /// <summary>Confirms enrollment with a current code; enables 2FA and returns recovery codes (shown once).</summary>
    [HttpPost("totp/enroll/confirm")]
    public async Task<IActionResult> ConfirmTotp([FromBody] TotpConfirmRequest request)
    {
        var userId = GetCurrentUserId();
        if (userId == null) return Unauthorized();

        var totp = await _context.UserTotps.FirstOrDefaultAsync(t => t.UserId == userId.Value);
        if (totp == null) return BadRequest("Start enrollment first.");
        if (totp.EnabledAt != null) return BadRequest("2FA is already enabled.");

        if (!_totp.VerifyCode(totp.EncryptedSecret, request.Code))
            return BadRequest("Invalid code. Check your authenticator app and try again.");

        var (plaintext, hashes) = _totp.GenerateRecoveryCodes();
        totp.RecoveryCodes = JsonSerializer.Serialize(hashes);
        totp.UsedRecoveryCodes = "[]";
        totp.EnabledAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();

        return Ok(new TotpConfirmResponse(plaintext));
    }

    /// <summary>Disables 2FA for the caller. Requires the account password plus a current code (or recovery code).</summary>
    [HttpPost("totp/disable")]
    public async Task<IActionResult> DisableTotp([FromBody] TotpDisableRequest request)
    {
        var userId = GetCurrentUserId();
        if (userId == null) return Unauthorized();

        var user = await _context.Users.FindAsync(userId.Value);
        if (user == null) return NotFound();
        if (!_passwordHasher.VerifyPassword(request.Password, user.PasswordHash))
            return BadRequest("Incorrect password.");

        var totp = await _context.UserTotps.FirstOrDefaultAsync(t => t.UserId == userId.Value);
        if (totp?.EnabledAt == null) return BadRequest("2FA is not enabled.");

        var ok = _totp.VerifyCode(totp.EncryptedSecret, request.Code);
        if (!ok)
        {
            // allow a recovery code as the second factor for disabling
            var remaining = JsonSerializer.Deserialize<List<string>>(totp.RecoveryCodes) ?? new();
            ok = remaining.Contains(_totp.HashRecoveryCode(request.Code));
        }
        if (!ok) return BadRequest("Invalid authentication code.");

        _context.UserTotps.Remove(totp);
        await _context.SaveChangesAsync();

        // Disabling 2FA invalidates any remembered devices for this user.
        await _trustedDevices.RevokeAllAsync(userId.Value);
        return Ok();
    }

    /// <summary>Lists the caller's remembered 2FA devices (for the 2FA-expiry window).</summary>
    [HttpGet("trusted-devices")]
    public async Task<IActionResult> GetTrustedDevices()
    {
        var userId = GetCurrentUserId();
        if (userId == null) return Unauthorized();
        var devices = await _trustedDevices.ListAsync(userId.Value);
        return Ok(devices.Select(d => new TrustedDeviceDto(
            d.Id, d.Label, d.CreatedAtUtc, d.LastSeenAtUtc, d.LastVerifiedAtUtc)));
    }

    /// <summary>Forgets one remembered device, forcing 2FA there on next login.</summary>
    [HttpDelete("trusted-devices/{id:guid}")]
    public async Task<IActionResult> RevokeTrustedDevice(Guid id)
    {
        var userId = GetCurrentUserId();
        if (userId == null) return Unauthorized();
        return await _trustedDevices.RevokeAsync(userId.Value, id) ? Ok() : NotFound();
    }

    /// <summary>Forgets all remembered devices for the caller.</summary>
    [HttpDelete("trusted-devices")]
    public async Task<IActionResult> RevokeAllTrustedDevices()
    {
        var userId = GetCurrentUserId();
        if (userId == null) return Unauthorized();
        var n = await _trustedDevices.RevokeAllAsync(userId.Value);
        return Ok(new { revoked = n });
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

public record TrustedDeviceDto(Guid Id, string? Label, DateTime CreatedAtUtc, DateTime LastSeenAtUtc, DateTime LastVerifiedAtUtc);

public record CreateApiTokenRequest(string Label, List<string>? Scopes, DateTime? ExpiresAt);

public record CreateApiTokenResponse(Guid Id, string Token, string Label);

public record ApiTokenDto(
    Guid Id,
    string Label,
    List<string> Scopes,
    DateTime CreatedAt,
    DateTime? LastUsedAt,
    string? LastUsedIp,
    DateTime? ExpiresAt);
