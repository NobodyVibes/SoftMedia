using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using SoftMedia.Server.Data;
using SoftMedia.Server.DTOs;
using SoftMedia.Server.Models;
using SoftMedia.Server.Services.Abstractions;
using SoftMedia.Server.Services.Identity;
using SoftMedia.Server.Services.Infrastructure;
using SoftMedia.Server.Services.Media;
using SoftMedia.Server.Services.Scanning;
using SoftMedia.Server.Services.Transcoding;

namespace SoftMedia.Server.Controllers;

[Route("api/v1/[controller]")]
[ApiController]
[AllowAnonymous] // Mixed auth: Login/Signup/Refresh/Logout are public; ChangePassword carries its own [Authorize].
public class AuthController : ControllerBase
{
    private readonly AppDbContext _context;
    private readonly IPasswordHasher _passwordHasher;
    private readonly ITokenService _tokenService;
    private readonly IRefreshTokenService _refreshTokens;
    private readonly ISettingsService _settingsService;
    private readonly IUserPreferencesService _userPreferencesService;
    private readonly ITotpService _totpService;
    private readonly ITrustedDeviceService _trustedDevices;
    private readonly IWebHostEnvironment _env;
    private readonly ILogger<AuthController> _logger;

    private const string DeviceCookieName = "tfaDevice";

    public AuthController(
        AppDbContext context,
        IPasswordHasher passwordHasher,
        ITokenService tokenService,
        IRefreshTokenService refreshTokens,
        ISettingsService settingsService,
        IUserPreferencesService userPreferencesService,
        ITotpService totpService,
        ITrustedDeviceService trustedDevices,
        IWebHostEnvironment env,
        ILogger<AuthController> logger)
    {
        _context = context;
        _passwordHasher = passwordHasher;
        _tokenService = tokenService;
        _refreshTokens = refreshTokens;
        _settingsService = settingsService;
        _userPreferencesService = userPreferencesService;
        _totpService = totpService;
        _trustedDevices = trustedDevices;
        _env = env;
        _logger = logger;
    }

    [EnableRateLimiting(Extensions.ServiceCollectionExtensions.AuthRateLimitPolicy)]
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
            // NOTE: `Forbid(string)` treats the string as an auth scheme name,
            // not a response body, and throws if the scheme isn't registered.
            // Return a real 403 with a human-readable message instead.
            return StatusCode(StatusCodes.Status403Forbidden, "Public signup is disabled.");
        }
        else if (signupSetting == "InviteOnly" && string.IsNullOrEmpty(request.InviteCode))
        {
            return BadRequest("Invite code is required.");
        }

        if (await _context.Users.AnyAsync(u => u.Username == request.Username))
        {
            return BadRequest("Username already exists.");
        }

        if (!string.IsNullOrEmpty(request.InviteCode))
        {
            var invite = await _context.Invites
                .Include(i => i.UsedBy)
                .FirstOrDefaultAsync(i => i.Code == request.InviteCode);

            if (invite == null) return BadRequest("Invalid invite code.");
            if (invite.IsRevoked) return BadRequest("This invite has been revoked.");
            if (invite.UsedAt != null) return BadRequest("This invite has already been used.");
            if (invite.ExpiresAt != null && invite.ExpiresAt < DateTime.UtcNow)
                return BadRequest("This invite has expired.");
        }

        var user = new User
        {
            Username = request.Username,
            PasswordHash = _passwordHasher.HashPassword(request.Password),
            Role = UserRole.User,
            CreatedAt = DateTime.UtcNow,
            FirstName = request.FirstName,
            LastName = request.LastName
        };

        if (!await _context.Users.AnyAsync())
        {
            user.Role = UserRole.Admin;
            user.IsApproved = true;
        }

        if (!string.IsNullOrEmpty(request.InviteCode))
        {
            user.IsApproved = true;
        }

        _context.Users.Add(user);

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
        await _userPreferencesService.InitializeDefaultsAsync(user.Id);

        var accessToken = _tokenService.GenerateAccessToken(user);
        var (refreshRaw, _) = await _refreshTokens.IssueAsync(user, ClientIp());
        SetRefreshTokenCookie(refreshRaw);

        return Ok(new AuthResponse(accessToken, await BuildUserDtoAsync(user, request.InviteCode, mustChangePassword: false)));
    }

    [EnableRateLimiting(Extensions.ServiceCollectionExtensions.AuthRateLimitPolicy)]
    [HttpPost("login")]
    public async Task<ActionResult<AuthResponse>> Login(LoginRequest request)
    {
        var user = await _context.Users.FirstOrDefaultAsync(u => u.Username == request.Username);
        if (user == null || !_passwordHasher.VerifyPassword(request.Password, user.PasswordHash))
        {
            return Unauthorized("Invalid username or password.");
        }
        if (user.IsBanned) return Unauthorized("This account has been banned.");
        if (user.IsDeleted) return Unauthorized("Invalid username or password.");
        if (!user.IsApproved) return Unauthorized("Account pending approval.");

        // P2-WI-005: if the user has TOTP enabled, do NOT issue tokens yet. Return a
        // short-lived challenge; the client completes login via POST /auth/2fa.
        var totp = await _context.UserTotps.FirstOrDefaultAsync(t => t.UserId == user.Id);
        if (totp?.EnabledAt != null)
        {
            // 2FA-expiry: if this device completed 2FA within the configured window,
            // skip the challenge. 0 (or no remembered device) always challenges.
            var expirationDays = await _settingsService.GetSettingAsync("TwoFactorExpirationDays", 0);
            var trusted = await _trustedDevices.FindValidAsync(user.Id, Request.Cookies[DeviceCookieName], expirationDays);
            if (trusted != null)
            {
                await _trustedDevices.TouchAsync(trusted);
                return Ok(await IssueLoginResponseAsync(user));
            }

            var challengeId = _totpService.CreateChallenge(user.Id);
            return Ok(new TwoFactorRequiredResponse(challengeId));
        }

        return Ok(await IssueLoginResponseAsync(user));
    }

    /// <summary>
    /// Completes a login that requires a TOTP second factor. The code may be a 6-digit
    /// TOTP code or a single-use recovery code.
    /// </summary>
    [EnableRateLimiting(Extensions.ServiceCollectionExtensions.TwoFactorRateLimitPolicy)]
    [HttpPost("2fa")]
    public async Task<ActionResult<AuthResponse>> CompleteTwoFactor([FromQuery] string challengeId, [FromBody] TwoFactorRequest request)
    {
        // challengeId travels in the query so the rate-limit policy can partition on it.
        var id = !string.IsNullOrEmpty(challengeId) ? challengeId : request.ChallengeId;
        if (!_totpService.TryConsumeChallenge(id, out var userId))
            return Unauthorized("2FA challenge expired or invalid. Please log in again.");

        var totp = await _context.UserTotps.FirstOrDefaultAsync(t => t.UserId == userId);
        var user = await _context.Users.FindAsync(userId);
        if (totp?.EnabledAt == null || user == null)
            return Unauthorized("2FA is not enabled for this account.");
        if (user.IsBanned || user.IsDeleted || !user.IsApproved)
            return Unauthorized("Account not eligible.");

        var verified = _totpService.VerifyCode(totp.EncryptedSecret, request.Code);
        if (!verified && !TryConsumeRecoveryCode(totp, request.Code))
            return Unauthorized("Invalid authentication code.");

        await _context.SaveChangesAsync(); // persist a consumed recovery code, if any
        _totpService.Complete(id);

        // 2FA-expiry: remember this device so future logins can skip 2FA until the window
        // elapses. Only when the admin has enabled a window (>0); otherwise we never skip.
        var expirationDays = await _settingsService.GetSettingAsync("TwoFactorExpirationDays", 0);
        if (expirationDays > 0)
        {
            var (_, rawToken) = await _trustedDevices.RememberAsync(
                user.Id, Request.Cookies[DeviceCookieName], Request.Headers.UserAgent.ToString(), ClientIp());
            SetDeviceCookie(rawToken, expirationDays);
        }

        return Ok(await IssueLoginResponseAsync(user));
    }

    /// Issues the access token + rotating refresh cookie and builds the AuthResponse.
    /// Shared by password-only login and TOTP-completed login.
    private async Task<AuthResponse> IssueLoginResponseAsync(SoftMedia.Server.Models.User user)
    {
        var accessToken = _tokenService.GenerateAccessToken(user);
        var (refreshRaw, _) = await _refreshTokens.IssueAsync(user, ClientIp());
        SetRefreshTokenCookie(refreshRaw);

        var usedInviteCode = await _context.Invites
            .Where(i => i.UsedById == user.Id)
            .Select(i => i.Code)
            .FirstOrDefaultAsync();

        return new AuthResponse(accessToken, await BuildUserDtoAsync(user, usedInviteCode, user.MustChangePassword));
    }

    /// Consumes a single-use recovery code (moves its hash to UsedRecoveryCodes).
    /// Returns true if the code matched an unused recovery code.
    private bool TryConsumeRecoveryCode(SoftMedia.Server.Models.UserTotp totp, string code)
    {
        var hash = _totpService.HashRecoveryCode(code);
        var remaining = System.Text.Json.JsonSerializer.Deserialize<List<string>>(totp.RecoveryCodes) ?? new();
        if (!remaining.Remove(hash)) return false;

        var used = System.Text.Json.JsonSerializer.Deserialize<List<string>>(totp.UsedRecoveryCodes) ?? new();
        used.Add(hash);
        totp.RecoveryCodes = System.Text.Json.JsonSerializer.Serialize(remaining);
        totp.UsedRecoveryCodes = System.Text.Json.JsonSerializer.Serialize(used);
        return true;
    }

    [Authorize]
    [HttpPost("change-password")]
    public async Task<IActionResult> ChangePassword(ChangePasswordRequest request)
    {
        var userIdString = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        if (userIdString == null || !Guid.TryParse(userIdString, out var userId))
        {
            return Unauthorized();
        }

        var user = await _context.Users.FindAsync(userId);
        if (user == null) return NotFound("User not found.");

        if (!_passwordHasher.VerifyPassword(request.OldPassword, user.PasswordHash))
        {
            return BadRequest("Invalid old password.");
        }

        user.PasswordHash = _passwordHasher.HashPassword(request.NewPassword);
        user.MustChangePassword = false;

        await _context.SaveChangesAsync();

        // Revoke every refresh token owned by this user. Any open session using
        // the old credentials now has to re-authenticate; its refresh cookie
        // still matches a DB row but that row is marked revoked.
        await _refreshTokens.RevokeAllForUserAsync(user.Id, RefreshTokenRevocationReason.PasswordChange);

        // A password change is a security event: forget remembered 2FA devices so the
        // next login re-challenges 2FA on every device.
        await _trustedDevices.RevokeAllAsync(user.Id);

        return Ok("Password changed successfully.");
    }

    [HttpPost("refresh-token")]
    public async Task<ActionResult<AuthResponse>> Refresh()
    {
        var raw = Request.Cookies["refreshToken"];
        if (string.IsNullOrEmpty(raw))
        {
            // Diagnostic: cookies that DID arrive (names only — values are
            // sensitive). Helps tell "browser dropped cookie" vs "wrong path".
            _logger.LogInformation(
                "Refresh failed: no refreshToken cookie. Cookies present: [{Cookies}]. " +
                "Path={Path}, IsHttps={IsHttps}, Origin={Origin}",
                string.Join(",", Request.Cookies.Keys),
                Request.Path,
                Request.IsHttps,
                Request.Headers.Origin.ToString());
            return Unauthorized("No refresh token provided.");
        }

        var validation = await _refreshTokens.ValidateAsync(raw);

        if (validation.IsReuse && validation.Token != null)
        {
            // Treat replayed-after-rotation as chain compromise. Revoke every
            // active token for this user and force a fresh login.
            _logger.LogWarning(
                "Refresh-token reuse detected for user {UserId} from IP {Ip}. Revoking chain.",
                validation.Token.UserId, ClientIp());
            await _refreshTokens.RevokeAllForUserAsync(
                validation.Token.UserId, RefreshTokenRevocationReason.ReuseDetected);
            ClearRefreshTokenCookie();
            return Unauthorized("Refresh token chain invalidated. Please login again.");
        }

        if (!validation.IsValid || validation.Token is null)
        {
            // Diagnostic: which validation branch failed?
            var reason = validation.Token is null
                ? "hash-not-in-db"
                : validation.Token.RevokedAt != null
                    ? $"revoked@{validation.Token.RevokedAt:O}/reason={validation.Token.ReasonRevoked}"
                    : $"expired@{validation.Token.ExpiresAt:O}";
            _logger.LogInformation(
                "Refresh failed: validation rejected ({Reason}). UserId={UserId}",
                reason, validation.Token?.UserId);

            ClearRefreshTokenCookie();
            return Unauthorized("Refresh token expired or invalid. Please login again.");
        }

        var user = await _context.Users.FindAsync(validation.Token.UserId);
        if (user is null || user.IsBanned || user.IsDeleted || !user.IsApproved)
        {
            _logger.LogInformation(
                "Refresh failed: account state. UserId={UserId} IsNull={IsNull} Banned={Banned} Deleted={Deleted} Approved={Approved}",
                validation.Token.UserId, user is null,
                user?.IsBanned, user?.IsDeleted, user?.IsApproved);
            await _refreshTokens.RevokeAsync(
                validation.Token,
                RefreshTokenRevocationReason.AccountSuspended,
                ClientIp());
            ClearRefreshTokenCookie();
            return Unauthorized("Account not eligible. Please login again.");
        }

        var (newRaw, _) = await _refreshTokens.RotateAsync(validation.Token, ClientIp());
        SetRefreshTokenCookie(newRaw);

        var accessToken = _tokenService.GenerateAccessToken(user);
        var usedInviteCode = await _context.Invites
            .Where(i => i.UsedById == user.Id)
            .Select(i => i.Code)
            .FirstOrDefaultAsync();

        return Ok(new AuthResponse(accessToken, await BuildUserDtoAsync(user, usedInviteCode, user.MustChangePassword)));
    }

    [HttpPost("logout")]
    public async Task<IActionResult> Logout()
    {
        var raw = Request.Cookies["refreshToken"];
        if (!string.IsNullOrEmpty(raw))
        {
            var validation = await _refreshTokens.ValidateAsync(raw);
            if (validation.Token != null && validation.Token.RevokedAt == null)
            {
                await _refreshTokens.RevokeAsync(
                    validation.Token,
                    RefreshTokenRevocationReason.Logout,
                    ClientIp());
            }
        }
        ClearRefreshTokenCookie();
        return Ok("Logged out.");
    }

    private async Task<UserDto> BuildUserDtoAsync(User user, string? usedInviteCode, bool mustChangePassword)
    {
        var ratings = string.IsNullOrEmpty(user.ContentRatings)
            ? new Dictionary<string, string>()
            : System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(
                  user.ContentRatings, (System.Text.Json.JsonSerializerOptions?)null)
              ?? new Dictionary<string, string>();

        var twoFactorEnabled = await _context.UserTotps
            .AnyAsync(t => t.UserId == user.Id && t.EnabledAt != null);

        return new UserDto(
            user.Id, user.Username, user.Role, user.MaxRating, user.CreatedAt,
            user.IsBanned, user.IsApproved, user.IsRejected,
            ratings,
            user.FirstName, user.LastName, user.CreatedByAdmin,
            usedInviteCode, mustChangePassword, twoFactorEnabled);
    }

    private string? ClientIp() => HttpContext.Connection.RemoteIpAddress?.ToString();

    private void SetRefreshTokenCookie(string rawToken)
    {
        // Secure gates on the actual request scheme: HTTPS requests set
        // Secure=true, HTTP requests don't. Setting Secure over HTTP causes
        // browsers and HttpClient's CookieContainer to silently drop the
        // cookie.
        //
        // SameSite=Lax: refresh cookie still rides on first-party fetches
        // and top-level navigations, but is blocked on third-party sub-
        // resource requests. Strict was tried first but interacted poorly
        // with Vite's dev proxy in some browser/version combinations,
        // dropping the cookie on POST to /auth/refresh-token even though
        // the call was technically same-origin. Lax is the standard refresh-
        // token-cookie posture per OAuth 2.1 / OWASP guidance: HttpOnly
        // protects against XSS exfiltration, Path scoping limits exposure
        // to /api/v1/auth/*, and Lax is sufficient against CSRF for POSTs
        // because browsers do not include Lax cookies on cross-site POSTs.
        var cookieOptions = new CookieOptions
        {
            HttpOnly = true,
            Expires = DateTime.UtcNow.AddDays(7),
            SameSite = SameSiteMode.Lax,
            Secure = Request.IsHttps,
            Path = "/api/v1/auth/"
        };
        Response.Cookies.Append("refreshToken", rawToken, cookieOptions);
    }

    private void ClearRefreshTokenCookie()
    {
        Response.Cookies.Delete("refreshToken", new CookieOptions
        {
            Path = "/api/v1/auth/"
        });
    }

    /// <summary>
    /// Sets the opaque "remembered device" token. Same posture as the refresh cookie
    /// (HttpOnly, Lax, Secure-on-HTTPS, scoped to /auth). The server enforces the real
    /// expiry from the DB row + the live setting; the cookie max-age is just a hint, so
    /// it's given a generous lifetime.
    /// </summary>
    private void SetDeviceCookie(string rawToken, int expirationDays)
    {
        Response.Cookies.Append(DeviceCookieName, rawToken, new CookieOptions
        {
            HttpOnly = true,
            Expires = DateTime.UtcNow.AddDays(Math.Max(expirationDays, 1) + 1),
            SameSite = SameSiteMode.Lax,
            Secure = Request.IsHttps,
            Path = "/api/v1/auth/"
        });
    }
}
