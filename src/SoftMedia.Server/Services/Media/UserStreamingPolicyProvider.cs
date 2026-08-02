using Microsoft.EntityFrameworkCore;
using SoftMedia.Server.Data;

namespace SoftMedia.Server.Services.Media;

/// <summary>
/// A user's admin-set streaming limits (QS-WI-002). All fields are nullable = "inherit the
/// server policy"; values ≤ 0 are normalized to null by the provider. Semantics (documented,
/// not changed — §2 of the streaming-quality plan):
/// override-wins — a set cap REPLACES the server's network (LAN/WAN) tier for this account
/// and may therefore exceed it. <see cref="RemoteMaxBitrateKbps"/> applies only off-LAN and,
/// when set, beats <see cref="MaxBitrateKbps"/> there.
/// </summary>
public sealed record UserStreamingPolicy(
    int? MaxBitrateKbps,
    int? RemoteMaxBitrateKbps,
    int? MaxResolution)
{
    public static readonly UserStreamingPolicy Empty = new(null, null, null);

    /// The bitrate cap this policy imposes for the given network class, or null when the
    /// user inherits the server's LAN/WAN tier. Off-LAN the remote variant wins when set.
    public int? EffectiveBitrateCap(bool isLan) =>
        !isLan && RemoteMaxBitrateKbps is > 0 ? RemoteMaxBitrateKbps : MaxBitrateKbps;
}

/// <summary>
/// Single read path for a user's streaming limits — TranscodeController (plan +
/// fabricated-sid master.m3u8) and StreamController (direct-play cap gate) all resolve
/// the same three columns through here, so the projection has one SQLite-verified shape.
/// </summary>
public interface IUserStreamingPolicyProvider
{
    Task<UserStreamingPolicy> GetAsync(Guid userId);
}

public class UserStreamingPolicyProvider : IUserStreamingPolicyProvider
{
    private readonly AppDbContext _context;

    public UserStreamingPolicyProvider(AppDbContext context) => _context = context;

    public async Task<UserStreamingPolicy> GetAsync(Guid userId)
    {
        var row = await _context.Users
            .Where(u => u.Id == userId)
            .Select(u => new { u.MaxStreamBitrateKbps, u.RemoteMaxStreamBitrateKbps, u.MaxStreamResolution })
            .FirstOrDefaultAsync();
        if (row == null) return UserStreamingPolicy.Empty;

        // 0 is the UI's "unlimited" sentinel (R-WI-009 kept null and 0 equivalent) — normalize
        // so callers only ever test "is > 0"-style nullability.
        return new UserStreamingPolicy(
            Normalize(row.MaxStreamBitrateKbps),
            Normalize(row.RemoteMaxStreamBitrateKbps),
            Normalize(row.MaxStreamResolution));
    }

    private static int? Normalize(int? value) => value is > 0 ? value : null;
}
