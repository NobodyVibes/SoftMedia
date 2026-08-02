using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Moq;
using SoftMedia.Server.Controllers;
using SoftMedia.Server.Data;
using SoftMedia.Server.Models;
using SoftMedia.Server.Services.Infrastructure;
using SoftMedia.Server.Services.Media;
using System.Security.Claims;
using Xunit;

namespace SoftMedia.Server.Tests.Controllers;

/// QS-WI-009 — GET /api/v1/me/streaming-limits on REAL SQLite (§5 standing constraint:
/// EF InMemory would evaluate a mistranslated shape client-side and prove nothing).
/// The endpoint mirrors StreamPlanService arbitration: override-wins per-user policy
/// (remote variant off-LAN), the remote-only network resolution ceiling, and the
/// server-wide MaxTranscodeResolution guardrail clamping on top. Denials are 404, never
/// 403 (anti-probe per SDD §6.2).
public class MeControllerStreamingLimitsTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;
    private readonly Mock<ISettingsService> _settings = new();

    public MeControllerStreamingLimitsTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;
        using var ctx = new AppDbContext(_options);
        ctx.Database.EnsureCreated();

        // Shipped defaults (§6.2 decision: 20 Mbps WAN / unlimited LAN).
        SetServerPolicy();
    }

    public void Dispose() => _connection.Dispose();

    private void SetServerPolicy(int wanKbps = 20000, int lanKbps = 0,
        string remoteMaxResolution = "original", string maxTranscodeResolution = "original")
    {
        _settings.Reset();
        _settings.Setup(s => s.GetSettingAsync("MaxStreamingBitrate", It.IsAny<int>())).ReturnsAsync(wanKbps);
        _settings.Setup(s => s.GetSettingAsync("MaxStreamingBitrateLan", It.IsAny<int>())).ReturnsAsync(lanKbps);
        _settings.Setup(s => s.GetSettingAsync("RemoteMaxResolution", It.IsAny<string>())).ReturnsAsync(remoteMaxResolution);
        _settings.Setup(s => s.GetSettingAsync("MaxTranscodeResolution", It.IsAny<string>())).ReturnsAsync(maxTranscodeResolution);
    }

    private Guid SeedUser(int? baseKbps = null, int? remoteKbps = null, int? maxResolution = null,
        bool isDeleted = false)
    {
        using var ctx = new AppDbContext(_options);
        var user = new User
        {
            Username = $"u-{Guid.NewGuid():N}", PasswordHash = "x", Role = UserRole.User, IsApproved = true,
            MaxStreamBitrateKbps = baseKbps,
            RemoteMaxStreamBitrateKbps = remoteKbps,
            MaxStreamResolution = maxResolution,
            IsDeleted = isDeleted,
        };
        ctx.Users.Add(user);
        ctx.SaveChanges();
        return user.Id;
    }

    private async Task<ActionResult<StreamingLimitsDto>> CallAsync(Guid callerId)
    {
        await using var ctx = new AppDbContext(_options);
        var controller = new MeController(ctx, new UserStreamingPolicyProvider(ctx), _settings.Object)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                        new[] { new Claim(ClaimTypes.NameIdentifier, callerId.ToString()) }, "test")),
                },
            },
        };
        return await controller.GetStreamingLimits();
    }

    private static StreamingLimitsDto Limits(ActionResult<StreamingLimitsDto> result)
    {
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        return Assert.IsType<StreamingLimitsDto>(ok.Value);
    }

    [Fact]
    public async Task ShippedDefaults_LanUnlimited_RemoteWanCap()
    {
        var id = SeedUser();

        var limits = Limits(await CallAsync(id));

        Assert.Equal(0, limits.Lan.MaxBitrateKbps);      // unlimited at home
        Assert.Equal(0, limits.Lan.MaxResolution);
        Assert.Equal(20000, limits.Remote.MaxBitrateKbps); // the shipped WAN cap
        Assert.Equal(0, limits.Remote.MaxResolution);
    }

    [Fact]
    public async Task NetworkTiers_ReflectLanCapAndRemoteResolution()
    {
        SetServerPolicy(wanKbps: 10000, lanKbps: 40000, remoteMaxResolution: "1080p");
        var id = SeedUser();

        var limits = Limits(await CallAsync(id));

        Assert.Equal(40000, limits.Lan.MaxBitrateKbps);
        Assert.Equal(0, limits.Lan.MaxResolution);       // RemoteMaxResolution is remote-only
        Assert.Equal(10000, limits.Remote.MaxBitrateKbps);
        Assert.Equal(1080, limits.Remote.MaxResolution);
    }

    [Fact]
    public async Task UserBaseCap_OverrideWins_BothTiers_EvenAboveWanCap()
    {
        // Deliberate semantic (§0/§2): the per-user cap REPLACES the network tier — 30 Mbps
        // beats the 10 Mbps WAN cap, it is not min'd against it.
        SetServerPolicy(wanKbps: 10000, lanKbps: 5000);
        var id = SeedUser(baseKbps: 30000);

        var limits = Limits(await CallAsync(id));

        Assert.Equal(30000, limits.Lan.MaxBitrateKbps);
        Assert.Equal(30000, limits.Remote.MaxBitrateKbps);
    }

    [Fact]
    public async Task UserRemoteVariant_AppliesToRemoteTierOnly()
    {
        SetServerPolicy(wanKbps: 20000);
        var id = SeedUser(baseKbps: 3000, remoteKbps: 8000);

        var limits = Limits(await CallAsync(id));

        Assert.Equal(3000, limits.Lan.MaxBitrateKbps);   // base cap at home
        Assert.Equal(8000, limits.Remote.MaxBitrateKbps); // remote variant wins away
    }

    [Fact]
    public async Task UserResolution_OverridesRemoteNetworkCeiling()
    {
        SetServerPolicy(remoteMaxResolution: "1080p");
        var id = SeedUser(maxResolution: 2160);

        var limits = Limits(await CallAsync(id));

        Assert.Equal(2160, limits.Lan.MaxResolution);
        Assert.Equal(2160, limits.Remote.MaxResolution); // override-wins over the 1080p network cap
    }

    [Fact]
    public async Task ServerTranscodeCeiling_ClampsOnTopOfEverything()
    {
        // MaxTranscodeResolution is the hardware guardrail, not a network cap — it clamps
        // on top of the user override on BOTH tiers.
        SetServerPolicy(remoteMaxResolution: "1080p", maxTranscodeResolution: "720p");
        var id = SeedUser(maxResolution: 2160);

        var limits = Limits(await CallAsync(id));

        Assert.Equal(720, limits.Lan.MaxResolution);
        Assert.Equal(720, limits.Remote.MaxResolution);
    }

    [Fact]
    public async Task UnknownUser_Returns404()
    {
        var result = await CallAsync(Guid.NewGuid());

        Assert.IsType<NotFoundResult>(result.Result);
    }

    [Fact]
    public async Task DeletedUser_Returns404_NotForbidden_AntiProbe()
    {
        // 404-over-403 (SDD §6.2): a deleted account is indistinguishable from a
        // nonexistent one — no 403 anywhere on this endpoint.
        var id = SeedUser(isDeleted: true);

        var result = await CallAsync(id);

        Assert.IsType<NotFoundResult>(result.Result);
    }
}
