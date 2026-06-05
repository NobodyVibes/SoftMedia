using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SoftMedia.Server.Data;
using SoftMedia.Server.Models;
using SoftMedia.Server.Services.Identity;
using Xunit;

namespace SoftMedia.Server.Tests.Services.Identity;

/// Verifies the 2FA "remember this device" window: a device is honoured only within the
/// configured expiration, the window is disabled at 0, tokens are reused/refreshed, and
/// revocation works. Uses a file-backed SQLite DB (RevokeAllAsync uses ExecuteDeleteAsync,
/// which the EF in-memory provider does not support).
public class TrustedDeviceServiceTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;
    private readonly Guid _userId = Guid.NewGuid();

    public TrustedDeviceServiceTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "sm-trusteddev-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
        _connection = new SqliteConnection($"Data Source={Path.Combine(_tempRoot, "t.db")}");
        _connection.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;
        using var ctx = new AppDbContext(_options);
        ctx.Database.EnsureCreated();
        ctx.Users.Add(new User
        {
            Id = _userId, Username = "u", PasswordHash = "x", Role = UserRole.User,
            IsApproved = true, CreatedAt = DateTime.UtcNow, FirstName = "F", LastName = "L", ContentRatings = "{}",
        });
        ctx.SaveChanges();
    }

    public void Dispose()
    {
        _connection.Dispose();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_tempRoot, true); } catch { }
    }

    private TrustedDeviceService New() => new(new AppDbContext(_options));

    [Fact]
    public async Task Remember_NewDevice_ThenFindValid_WithinWindow()
    {
        var (_, token) = await New().RememberAsync(_userId, null, "Mozilla/5.0 (Windows) Chrome/120", "1.2.3.4");

        var found = await New().FindValidAsync(_userId, token, expirationDays: 30);
        Assert.NotNull(found);
        Assert.Equal("Chrome on Windows", found!.Label);
    }

    [Fact]
    public async Task FindValid_WindowDisabled_ReturnsNull()
    {
        var (_, token) = await New().RememberAsync(_userId, null, null, null);
        Assert.Null(await New().FindValidAsync(_userId, token, expirationDays: 0));
    }

    [Fact]
    public async Task FindValid_MissingToken_ReturnsNull()
    {
        Assert.Null(await New().FindValidAsync(_userId, null, expirationDays: 30));
        Assert.Null(await New().FindValidAsync(_userId, "not-a-real-token", expirationDays: 30));
    }

    [Fact]
    public async Task FindValid_PastWindow_ReturnsNull()
    {
        var (device, token) = await New().RememberAsync(_userId, null, null, null);

        // Backdate the last verification to 10 days ago.
        await using (var ctx = new AppDbContext(_options))
        {
            var row = await ctx.TrustedDevices.FindAsync(device.Id);
            row!.LastVerifiedAtUtc = DateTime.UtcNow.AddDays(-10);
            await ctx.SaveChangesAsync();
        }

        Assert.Null(await New().FindValidAsync(_userId, token, expirationDays: 5));   // outside 5-day window
        Assert.NotNull(await New().FindValidAsync(_userId, token, expirationDays: 30)); // inside 30-day window
    }

    [Fact]
    public async Task Remember_ExistingToken_ReusesRow_AndRefreshes()
    {
        var (device, token) = await New().RememberAsync(_userId, null, null, null);
        await using (var ctx = new AppDbContext(_options))
        {
            var row = await ctx.TrustedDevices.FindAsync(device.Id);
            row!.LastVerifiedAtUtc = DateTime.UtcNow.AddDays(-3);
            await ctx.SaveChangesAsync();
        }

        var (device2, token2) = await New().RememberAsync(_userId, token, null, null);

        Assert.Equal(token, token2);          // same token reused
        Assert.Equal(device.Id, device2.Id);  // same row
        await using var verify = new AppDbContext(_options);
        Assert.Equal(1, await verify.TrustedDevices.CountAsync()); // not duplicated
        Assert.True((DateTime.UtcNow - device2.LastVerifiedAtUtc).TotalMinutes < 1); // refreshed to now
    }

    [Fact]
    public async Task Revoke_And_RevokeAll()
    {
        var (d1, _) = await New().RememberAsync(_userId, null, null, null);
        await New().RememberAsync(_userId, null, null, null);

        Assert.True(await New().RevokeAsync(_userId, d1.Id));
        Assert.False(await New().RevokeAsync(_userId, Guid.NewGuid())); // unknown id

        await using (var ctx = new AppDbContext(_options))
            Assert.Equal(1, await ctx.TrustedDevices.CountAsync());

        var removed = await New().RevokeAllAsync(_userId);
        Assert.Equal(1, removed);
        await using (var ctx = new AppDbContext(_options))
            Assert.Equal(0, await ctx.TrustedDevices.CountAsync());
    }
}
