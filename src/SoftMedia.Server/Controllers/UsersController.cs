using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SoftMedia.Server.Data;
using SoftMedia.Server.DTOs;
using SoftMedia.Server.Models;
using SoftMedia.Server.Services.Abstractions;
using SoftMedia.Server.Services.Identity;
using SoftMedia.Server.Services.Infrastructure;
using SoftMedia.Server.Services.Media;
using SoftMedia.Server.Services.Scanning;
using SoftMedia.Server.Services.Security.ContentRating;
using SoftMedia.Server.Services.Transcoding;
using System.Security.Claims;

namespace SoftMedia.Server.Controllers;

[Route("api/v1/[controller]")]
[ApiController]
[Authorize(Roles = "Admin")]
public class UsersController : ControllerBase
{
    private readonly AppDbContext _context;
    private readonly IPasswordHasher _passwordHasher;
    private readonly IUserPreferencesService _userPreferencesService;
    private readonly IRefreshTokenService _refreshTokens;
    private readonly ITrustedDeviceService _trustedDevices;
    private readonly IUserEligibilityCache _eligibilityCache;

    public UsersController(
        AppDbContext context,
        IPasswordHasher passwordHasher,
        IUserPreferencesService userPreferencesService,
        IRefreshTokenService refreshTokens,
        ITrustedDeviceService trustedDevices,
        IUserEligibilityCache eligibilityCache)
    {
        _context = context;
        _passwordHasher = passwordHasher;
        _userPreferencesService = userPreferencesService;
        _refreshTokens = refreshTokens;
        _trustedDevices = trustedDevices;
        _eligibilityCache = eligibilityCache;
    }

    /// <summary>
    /// Audit wave-2 WS-3 (H-2/L-6): revoke every refresh token and remembered-2FA device for a
    /// user so an admin action (password reset, ban, delete, deny/un-approve) actually evicts any
    /// live session. Mirrors the self-service AuthController.ChangePassword revocation — without
    /// this, a stolen refresh-token chain keeps minting access tokens after the "fix".
    /// </summary>
    private async Task RevokeUserSessionsAsync(Guid userId, string reason)
    {
        await _refreshTokens.RevokeAllForUserAsync(userId, reason);
        await _trustedDevices.RevokeAllAsync(userId);
    }

    [HttpGet]
    public async Task<ActionResult<IEnumerable<UserDto>>> GetUsers()
    {
        var users = await _context.Users
            .Where(u => !u.IsDeleted)
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
                    : System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(u.ContentRatings, (System.Text.Json.JsonSerializerOptions?)null) ?? new Dictionary<string, string>(),
                u.FirstName,
                u.LastName,
                u.CreatedByAdmin,
                _context.Invites.Where(i => i.UsedById == u.Id).Select(i => i.Code).FirstOrDefault(),
                u.MustChangePassword,
                _context.UserTotps.Any(t => t.UserId == u.Id && t.EnabledAt != null),
                u.MaxStreamBitrateKbps ?? 0 // R-WI-009 (null and 0 both mean unlimited)
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

        // Audit wave-2 L-7: enforce the same minimum password policy on admin-created accounts as
        // signup and reset already do — otherwise a family account could be seeded empty/1-char.
        if (PasswordPolicy.Validate(request.Password) is { } pwError)
        {
            return BadRequest(pwError);
        }

        var user = new User
        {
            Username = request.Username,
            PasswordHash = _passwordHasher.HashPassword(request.Password),
            Role = role,
            IsApproved = true, // Admin created users are auto-approved
            FirstName = request.FirstName,
            LastName = request.LastName,
            CreatedByAdmin = true
        };

        // R-WI-011: the admin may set content ceilings AT creation (visible in the modal).
        // Omitted/empty = NO restrictions — the maintainer-decided default; the old model
        // default silently capped every new user at PG-13 movies.
        if (ApplyContentRatings(user, request.ContentRatings) is { } ratingsError)
        {
            return BadRequest(ratingsError);
        }

        _context.Users.Add(user);
        await _context.SaveChangesAsync();

        // Initialize default preferences for the new user
        await _userPreferencesService.InitializeDefaultsAsync(user.Id);

        return Ok(new UserDto(
            user.Id,
            user.Username,
            user.Role,
            user.MaxRating,
            user.CreatedAt,
            user.IsBanned,
            user.IsApproved,
            user.IsRejected,
            System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(user.ContentRatings)
                ?? new Dictionary<string, string>(),
            user.FirstName,
            user.LastName,
            user.CreatedByAdmin,
            null,
            user.MustChangePassword,
            false, // newly created account has no 2FA enrollment yet
            user.MaxStreamBitrateKbps ?? 0 // R-WI-009 (0 for a new account)
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

        if (ApplyContentRatings(user, request.ContentRatings) is { } ratingsError)
        {
            return BadRequest(ratingsError);
        }
        await _context.SaveChangesAsync();

        return Ok();
    }

    /// <summary>
    /// R-WI-011 — single write path for a user's content ceilings. Strips empty entries
    /// (the ratings modal posts "" for "None (Unrestricted)"), validates labels against
    /// <see cref="RatingTables"/> (unknown labels FAIL OPEN downstream, so a typo'd cap
    /// would silently unrestrict — reject it here instead), and keeps the legacy
    /// <see cref="User.MaxRating"/> in sync with the map's Movie entry. Without that sync,
    /// choosing "None (Unrestricted)" for movies was a lie: the empty map entry fell back
    /// to the old invisible MaxRating="PG-13". Returns an error message, or null on success.
    /// Public so tests can drive it directly (project convention; no InternalsVisibleTo).
    /// </summary>
    public static string? ApplyContentRatings(User user, Dictionary<string, string>? contentRatings)
    {
        var tables = new Dictionary<string, IReadOnlyList<string>>
        {
            ["Movie"] = RatingTables.Movie,
            ["TV"] = RatingTables.Tv,
            ["Game"] = RatingTables.Game,
        };

        var cleaned = new Dictionary<string, string>();
        foreach (var (type, label) in contentRatings ?? new Dictionary<string, string>())
        {
            if (string.IsNullOrWhiteSpace(label)) continue; // "" = unrestricted for that type
            if (!tables.TryGetValue(type, out var table))
            {
                return $"Unknown content-rating type '{type}'. Valid types: Movie, TV, Game.";
            }
            var canonical = table.FirstOrDefault(t => string.Equals(t, label, StringComparison.OrdinalIgnoreCase));
            if (canonical == null)
            {
                return $"Unknown {type} rating '{label}'. Valid: {string.Join(", ", table)}.";
            }
            cleaned[type] = canonical; // store canonical casing so displays are consistent
        }

        user.ContentRatings = System.Text.Json.JsonSerializer.Serialize(cleaned);
        user.MaxRating = cleaned.GetValueOrDefault("Movie") ?? "";
        return null;
    }

    /// <summary>
    /// R-WI-009 — set a user's streaming bitrate cap (kbps; 0 = unlimited). Admin-only (the whole
    /// controller is <c>[Authorize(Roles="Admin")]</c>). Enforced at plan time by
    /// <see cref="Services.Media.StreamPlanService"/> / TranscodeController since P1-WI-003.
    /// </summary>
    [HttpPut("{id}/streaming")]
    public async Task<IActionResult> UpdateUserStreaming(Guid id, UpdateUserStreamingRequest request)
    {
        if (request.MaxStreamBitrateKbps < 0)
        {
            return BadRequest("Bitrate cap cannot be negative (use 0 for unlimited).");
        }

        var user = await _context.Users.FindAsync(id);
        if (user == null)
        {
            return NotFound("User not found.");
        }

        // Clamp the upper bound to the server's absolute streaming ceiling so a typo can't store an
        // absurd value; the plan computation clamps again at request time.
        user.MaxStreamBitrateKbps = Math.Min(request.MaxStreamBitrateKbps, 100_000);
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

        // AA-WI-011: eligibility changed (either direction) — drop the cached verdict so
        // live media/cast tokens see it on the next request, not after the cache TTL.
        _eligibilityCache.Invalidate(user.Id);

        // Audit wave-2 L-6: banning must immediately cut off live sessions (refresh chain +
        // remembered 2FA device), otherwise a stateless access/refresh token outlives the ban.
        if (request.IsBanned)
        {
            await RevokeUserSessionsAsync(user.Id, RefreshTokenRevocationReason.AccountSuspended);
        }

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

        _eligibilityCache.Invalidate(user.Id); // AA-WI-011



        // Audit wave-2 L-6: un-approving is an account-suspension — evict live sessions.
        if (!request.IsApproved)
        {
            await RevokeUserSessionsAsync(user.Id, RefreshTokenRevocationReason.AccountSuspended);
        }

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

        _eligibilityCache.Invalidate(user.Id); // AA-WI-011

        // Audit wave-2 L-6: denying revokes any session the user established before denial.
        await RevokeUserSessionsAsync(user.Id, RefreshTokenRevocationReason.AccountSuspended);

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

        // Soft Delete Implementation
        user.IsDeleted = true;
        user.DeletedAt = DateTime.UtcNow;
        
        // Rename user to free up the username for future use
        // Format: original_deleted_guid
        user.Username = $"{user.Username}_deleted_{Guid.NewGuid().ToString().Substring(0, 8)}";

        // We DO NOT need to nullify UsedById on Invites anymore, because the user row still exists!
        // This preserves the foreign key integrity and the history.
        
        // However, to prevent confusion in the UI (where we show UsedByUsername), we should append (Deleted)
        // This distinguishes this "johndoe" from any future "johndoe"
        var usedInvites = await _context.Invites.Where(i => i.UsedById == id).ToListAsync();
        foreach (var invite in usedInvites)
        {
            if (!string.IsNullOrEmpty(invite.UsedByUsername) && !invite.UsedByUsername.Contains("(Deleted)"))
            {
                invite.UsedByUsername = $"{invite.UsedByUsername} (Deleted)";
            }
        }

        await _context.SaveChangesAsync();

        // Audit wave-2 L-6: a deleted account's sessions must die immediately.
        await RevokeUserSessionsAsync(id, RefreshTokenRevocationReason.AccountSuspended);
        _eligibilityCache.Invalidate(id); // AA-WI-011

        return Ok();
    }
    /// <summary>
    /// Wave C — returns the user's per-library allow-list. Empty array means
    /// "unrestricted" (the default). Admin-only.
    /// </summary>
    [HttpGet("{id}/library-access")]
    public async Task<ActionResult<List<Guid>>> GetUserLibraryAccess(Guid id)
    {
        var userExists = await _context.Users.AnyAsync(u => u.Id == id);
        if (!userExists) return NotFound("User not found.");

        var ids = await _context.UserLibraryAccess
            .AsNoTracking()
            .Where(a => a.UserId == id)
            .Select(a => a.LibraryId)
            .ToListAsync();
        return Ok(ids);
    }

    /// <summary>
    /// Wave C — replaces the user's per-library allow-list. An empty array
    /// clears all rows (= unrestricted). Admin-only. Targeting an admin user
    /// is rejected because admins always bypass ACL.
    /// </summary>
    [HttpPut("{id}/library-access")]
    public async Task<IActionResult> SetUserLibraryAccess(Guid id, SetLibraryAccessRequest request)
    {
        var user = await _context.Users.FindAsync(id);
        if (user == null) return NotFound("User not found.");
        if (user.Role == UserRole.Admin)
            return BadRequest("Admins always have access to all libraries.");

        request.LibraryIds ??= new List<Guid>();

        // Validate all library IDs exist BEFORE we mutate anything so the
        // request is atomic — partial application would leave a confusing
        // half-applied ACL.
        if (request.LibraryIds.Count > 0)
        {
            var validIds = await _context.Libraries
                .Where(l => request.LibraryIds.Contains(l.Id))
                .Select(l => l.Id)
                .ToListAsync();
            var unknown = request.LibraryIds.Except(validIds).ToList();
            if (unknown.Count > 0)
                return BadRequest($"Unknown library IDs: {string.Join(", ", unknown)}");
        }

        var existing = await _context.UserLibraryAccess
            .Where(a => a.UserId == id)
            .ToListAsync();
        _context.UserLibraryAccess.RemoveRange(existing);

        foreach (var libraryId in request.LibraryIds.Distinct())
        {
            _context.UserLibraryAccess.Add(new UserLibraryAccess
            {
                UserId = id,
                LibraryId = libraryId
            });
        }

        await _context.SaveChangesAsync();
        return Ok();
    }

    [HttpPut("{id}/password")]
    public async Task<IActionResult> ResetUserPassword(Guid id, ResetUserPasswordRequest request)
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

        // Audit L2: enforce the minimum password policy on admin-set passwords too.
        if (SoftMedia.Server.Services.Identity.PasswordPolicy.Validate(request.NewPassword) is { } pwError)
        {
            return BadRequest(pwError);
        }

        // Prevent resetting your own password via this admin endpoint (optional, but good practice to force them to use the normal change password flow)
        // However, for "Manual Password Change" by admin, maybe they SHOULD be able to change their own?
        // Let's allow it for now, as it's an admin action.

        user.PasswordHash = _passwordHasher.HashPassword(request.NewPassword);
        
        // If the user was forced to change password, maybe we should clear that flag?
        // Or maybe we leave it, assuming the admin set a temporary password and wants the user to change it again?
        // Let's assume the admin is setting a permanent password or communicating it to the user.
        // If we want to force them to change it again, the admin can toggle that flag separately (if we had an endpoint for it).
        // For now, let's NOT clear MustChangePassword automatically, unless we want to treat this as a "fix".
        // Actually, if an admin resets it, the user probably knows the new password.
        // Let's clear MustChangePassword so they aren't stuck in a loop if that was the issue.
        user.MustChangePassword = false;

        await _context.SaveChangesAsync();

        // Audit wave-2 H-2: an admin password reset is the canonical "the account is compromised"
        // response — it MUST evict the attacker's live sessions, not just rotate the hash.
        await RevokeUserSessionsAsync(user.Id, RefreshTokenRevocationReason.PasswordChange);

        return Ok();
    }
}
