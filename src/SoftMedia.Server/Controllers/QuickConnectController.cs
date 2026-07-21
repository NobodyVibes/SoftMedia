using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using SoftMedia.Server.Data;
using SoftMedia.Server.DTOs;
using SoftMedia.Server.Services.Identity;
using SoftMedia.Server.Services.Infrastructure;

namespace SoftMedia.Server.Controllers;

/// <summary>
/// NR-WI-006 — Quick Connect device pairing. Flow: a device POSTs /initiate (anonymous,
/// rate-limited) and shows the 6-char code; the user enters the code in their logged-in
/// web session (GET /pending/{code} to review the device, POST /authorize to approve);
/// the device polls GET /state with its private secret and receives tokens exactly once
/// via the NR-WI-005 body-delivery flow. The whole feature is opt-in via the
/// EnableQuickConnect setting (default off) — disabled means every endpoint 404s.
/// </summary>
[ApiController]
[Route("api/v1/quickconnect")]
// Class-level default is the STRICT posture (full session, the requirement of the
// approve endpoints); the two device-side endpoints opt out with [AllowAnonymous].
// Never invert this (class [AllowAnonymous] suppresses action [Authorize] — the
// AuthController lesson).
[Authorize(Policy = ScopePolicies.FullSession)]
public class QuickConnectController : ControllerBase
{
    private readonly IQuickConnectService _quickConnect;
    private readonly ISettingsService _settings;
    private readonly ITokenService _tokens;
    private readonly IRefreshTokenService _refreshTokens;
    private readonly AppDbContext _context;
    private readonly ILogger<QuickConnectController> _logger;

    public QuickConnectController(
        IQuickConnectService quickConnect,
        ISettingsService settings,
        ITokenService tokens,
        IRefreshTokenService refreshTokens,
        AppDbContext context,
        ILogger<QuickConnectController> logger)
    {
        _quickConnect = quickConnect;
        _settings = settings;
        _tokens = tokens;
        _refreshTokens = refreshTokens;
        _context = context;
        _logger = logger;
    }

    private async Task<bool> IsEnabledAsync() =>
        string.Equals(await _settings.GetSettingAsync("EnableQuickConnect", "false"), "true", StringComparison.OrdinalIgnoreCase);

    private string? ClientIp() => HttpContext.Connection.RemoteIpAddress?.ToString();

    /// <summary>Device-side: start a pairing and get a code to display.</summary>
    [AllowAnonymous]
    [EnableRateLimiting(Extensions.ServiceCollectionExtensions.QuickConnectRateLimitPolicy)]
    [HttpPost("initiate")]
    public async Task<ActionResult<QuickConnectInitiateResponse>> Initiate(QuickConnectInitiateRequest? request)
    {
        if (!await IsEnabledAsync()) return NotFound();

        var initiation = _quickConnect.Initiate(request?.DeviceName, ClientIp());
        if (initiation is null)
        {
            // Store full — the anonymous surface is under pressure; tell honest
            // clients to back off rather than 404ing (which reads as "disabled").
            return StatusCode(StatusCodes.Status503ServiceUnavailable, "Too many pending pairings. Try again shortly.");
        }

        return Ok(new QuickConnectInitiateResponse(
            initiation.Code, initiation.Secret, initiation.ExpiresInSeconds, PollIntervalSeconds: 3));
    }

    /// <summary>
    /// Device-side poll. Pending → {status:"Pending"}; approved → tokens, exactly once;
    /// unknown/expired/already-claimed → 404 (terminal for the poller).
    /// </summary>
    [AllowAnonymous]
    [EnableRateLimiting(Extensions.ServiceCollectionExtensions.QuickConnectRateLimitPolicy)]
    [HttpGet("state")]
    public async Task<ActionResult<QuickConnectStateResponse>> State([FromQuery] string secret)
    {
        if (!await IsEnabledAsync()) return NotFound();
        if (string.IsNullOrEmpty(secret)) return NotFound();

        var claim = _quickConnect.TryClaim(secret);
        switch (claim.Status)
        {
            case QuickConnectClaimStatus.Pending:
                return Ok(new QuickConnectStateResponse("Pending"));

            case QuickConnectClaimStatus.Approved:
                // Eligibility re-check at claim time — the account may have been
                // banned/deleted between approval and this poll.
                var user = await _context.Users.FindAsync(claim.UserId!.Value);
                if (user is null || user.IsBanned || user.IsDeleted || !user.IsApproved)
                {
                    _logger.LogWarning("Quick Connect claim rejected: user {UserId} no longer eligible", claim.UserId);
                    return NotFound();
                }

                var accessToken = _tokens.GenerateAccessToken(user);
                var (refreshRaw, _) = await _refreshTokens.IssueAsync(user, ClientIp());
                _logger.LogInformation(
                    "Quick Connect pairing completed for user {UserId} from {Ip}", user.Id, ClientIp());
                // NR-WI-005 body delivery: the paired device has no cookie jar.
                return Ok(new QuickConnectStateResponse("Approved", accessToken, refreshRaw));

            default:
                return NotFound();
        }
    }

    /// <summary>
    /// User-side: review a pending code before approving (device name + IP + age).
    /// Full session only (class-level policy) — an API token or media/cast token
    /// must never approve a device.
    /// </summary>
    [HttpGet("pending/{code}")]
    public async Task<ActionResult<QuickConnectPendingResponse>> Pending(string code)
    {
        if (!await IsEnabledAsync()) return NotFound();

        var pending = _quickConnect.PeekPending(code);
        if (pending is null) return NotFound();

        return Ok(new QuickConnectPendingResponse(
            pending.Code, pending.DeviceName, pending.RequestIp, pending.CreatedAt));
    }

    /// <summary>User-side: approve a pending code, binding the device to the caller's account.</summary>
    [HttpPost("authorize")]
    public async Task<IActionResult> Authorize(QuickConnectAuthorizeRequest request)
    {
        if (!await IsEnabledAsync()) return NotFound();

        var sub = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
                 ?? User.FindFirst("sub")?.Value;
        if (!Guid.TryParse(sub, out var userId)) return Unauthorized();

        if (string.IsNullOrWhiteSpace(request.Code) || !_quickConnect.Authorize(request.Code, userId))
        {
            return NotFound("Code not found or expired.");
        }

        // Audit trail: who approved what, from where.
        _logger.LogInformation(
            "Quick Connect code {Code} authorized by user {UserId} from {Ip}",
            request.Code, userId, ClientIp());
        return NoContent();
    }
}
