using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SoftMedia.Server.Data;
using SoftMedia.Server.Models;
using SoftMedia.Server.Services.Media;
using Xunit;

namespace SoftMedia.Server.Tests.Services.Media;

/// QS-WI-002 — the per-user streaming-limits projection on REAL SQLite (§5 standing
/// constraint: EF InMemory would evaluate any untranslatable shape client-side and prove
/// nothing), plus the pure network-tier selection on the policy record.
public class UserStreamingPolicyProviderTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;

    public UserStreamingPolicyProviderTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;
        using var ctx = new AppDbContext(_options);
        ctx.Database.EnsureCreated();
    }

    public void Dispose() => _connection.Dispose();

    private Guid SeedUser(int? baseKbps, int? remoteKbps, int? maxResolution)
    {
        using var ctx = new AppDbContext(_options);
        var user = new User
        {
            Username = $"u-{Guid.NewGuid():N}", PasswordHash = "x", Role = UserRole.User, IsApproved = true,
            MaxStreamBitrateKbps = baseKbps,
            RemoteMaxStreamBitrateKbps = remoteKbps,
            MaxStreamResolution = maxResolution,
        };
        ctx.Users.Add(user);
        ctx.SaveChanges();
        return user.Id;
    }

    [Fact]
    public async Task Get_ReturnsAllThreeLimits()
    {
        var id = SeedUser(3000, 8000, 1080);
        await using var ctx = new AppDbContext(_options);

        var policy = await new UserStreamingPolicyProvider(ctx).GetAsync(id);

        Assert.Equal(3000, policy.MaxBitrateKbps);
        Assert.Equal(8000, policy.RemoteMaxBitrateKbps);
        Assert.Equal(1080, policy.MaxResolution);
    }

    [Fact]
    public async Task Get_NormalizesZeroAndNullToNull()
    {
        // R-WI-009 convention: 0 and null both mean unlimited — callers only see null.
        var id = SeedUser(0, null, 0);
        await using var ctx = new AppDbContext(_options);

        var policy = await new UserStreamingPolicyProvider(ctx).GetAsync(id);

        Assert.Null(policy.MaxBitrateKbps);
        Assert.Null(policy.RemoteMaxBitrateKbps);
        Assert.Null(policy.MaxResolution);
    }

    [Fact]
    public async Task Get_UnknownUser_ReturnsEmptyPolicy()
    {
        await using var ctx = new AppDbContext(_options);

        var policy = await new UserStreamingPolicyProvider(ctx).GetAsync(Guid.NewGuid());

        Assert.Equal(UserStreamingPolicy.Empty, policy);
    }

    [Theory]
    // Off-LAN: the remote variant wins when set, else the base cap.
    [InlineData(false, 3000, 8000, 8000)]
    [InlineData(false, 3000, null, 3000)]
    [InlineData(false, null, 8000, 8000)]
    // On LAN: the remote variant never applies.
    [InlineData(true, 3000, 8000, 3000)]
    [InlineData(true, null, 8000, null)]
    [InlineData(true, null, null, null)]
    public void EffectiveBitrateCap_PicksTheRightVariantPerNetwork(
        bool isLan, int? baseKbps, int? remoteKbps, int? expected)
    {
        var policy = new UserStreamingPolicy(baseKbps, remoteKbps, null);
        Assert.Equal(expected, policy.EffectiveBitrateCap(isLan));
    }
}
