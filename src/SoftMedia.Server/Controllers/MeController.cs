using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SoftMedia.Server.Data;
using SoftMedia.Server.Services.Infrastructure;
using SoftMedia.Server.Services.Media;
using System.Security.Claims;

namespace SoftMedia.Server.Controllers;

/// <summary>
/// QS-WI-009 — self-service, read-only views of the CALLER's effective server policy.
/// Backs the client settings page's "What the server allows you" line, so users can see
/// the ceiling their asks will be clamped to without a failed play teaching them.
/// </summary>
[ApiController]
[Route("api/v1/me")]
[Authorize]
public class MeController : ControllerBase
{
    private readonly AppDbContext _context;
    private readonly IUserStreamingPolicyProvider _userPolicies;
    private readonly ISettingsService _settingsService;

    public MeController(AppDbContext context, IUserStreamingPolicyProvider userPolicies,
        ISettingsService settingsService)
    {
        _context = context;
        _userPolicies = userPolicies;
        _settingsService = settingsService;
    }

    /// <summary>
    /// The caller's EFFECTIVE streaming ceilings per network tier, mirroring the
    /// StreamPlanService arbitration exactly (override-wins: a per-user cap REPLACES the
    /// network tier and may exceed it; the server-wide MaxTranscodeResolution guardrail
    /// still clamps on top of either resolution ceiling). 0 = unlimited. Read-only by
    /// design — the writable knobs stay the admin's (§4 of the streaming-quality plan).
    /// </summary>
    [HttpGet("streaming-limits")]
    public async Task<ActionResult<StreamingLimitsDto>> GetStreamingLimits()
    {
        var userId = GetCurrentUserId();
        if (userId == null) return Unauthorized();

        // 404-over-403 anti-probe rule (SDD §6.2): a deleted account answers exactly
        // like a nonexistent one.
        var exists = await _context.Users.AnyAsync(u => u.Id == userId.Value && !u.IsDeleted);
        if (!exists) return NotFound();

        var policy = await _userPolicies.GetAsync(userId.Value);

        var wanBitrate = await _settingsService.GetSettingAsync("MaxStreamingBitrate", 20000);
        var lanBitrate = await _settingsService.GetSettingAsync("MaxStreamingBitrateLan", 0);
        var remoteMaxResolution = await _settingsService.GetSettingAsync("RemoteMaxResolution", "original");
        var maxTranscodeResolution = await _settingsService.GetSettingAsync("MaxTranscodeResolution", "original");

        // Same label→height authority the fabricated-sid enforcement uses.
        var serverCeiling = ParseHeight(maxTranscodeResolution);
        var remoteNetworkCeiling = ParseHeight(remoteMaxResolution);

        // Bitrate: per-user cap (remote variant off-LAN) replaces the network tier when set.
        var lanBitrateCap = policy.EffectiveBitrateCap(isLan: true) ?? NullIfUnlimited(lanBitrate);
        var remoteBitrateCap = policy.EffectiveBitrateCap(isLan: false) ?? NullIfUnlimited(wanBitrate);

        // Resolution: the per-user ceiling replaces the network (remote-only) ceiling;
        // MaxTranscodeResolution clamps on top of whichever won.
        var lanResolution = MinCeiling(policy.MaxResolution, serverCeiling);
        var remoteResolution = MinCeiling(policy.MaxResolution ?? remoteNetworkCeiling, serverCeiling);

        return Ok(new StreamingLimitsDto(
            new StreamingLimitsTierDto(lanBitrateCap ?? 0, lanResolution ?? 0),
            new StreamingLimitsTierDto(remoteBitrateCap ?? 0, remoteResolution ?? 0)));
    }

    /// <summary>Quality label → height; null = uncapped ("original"/"auto"/unknown). The
    /// shared QualityLabels authority keeps the display in lockstep with enforcement.</summary>
    private static int? ParseHeight(string? label) => QualityLabels.HeightOrNull(label);

    private static int? NullIfUnlimited(int kbps) => kbps > 0 ? kbps : null;

    private static int? MinCeiling(int? a, int? b) => (a, b) switch
    {
        (null, null) => null,
        (null, _) => b,
        (_, null) => a,
        _ => Math.Min(a.Value, b.Value),
    };

    private Guid? GetCurrentUserId()
    {
        var userIdString = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return Guid.TryParse(userIdString, out var userId) ? userId : null;
    }
}

/// <summary>One network tier's effective ceilings; 0 = unlimited.</summary>
public record StreamingLimitsTierDto(int MaxBitrateKbps, int MaxResolution);

/// <summary>The caller's effective streaming ceilings at home (LAN) and away (remote).</summary>
public record StreamingLimitsDto(StreamingLimitsTierDto Lan, StreamingLimitsTierDto Remote);
