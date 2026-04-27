using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SoftMedia.Server.Data;
using SoftMedia.Server.Models;
using SoftMedia.Server.Services.Background;
using Xunit;

namespace SoftMedia.Server.Tests.Services.Background;

/// Verifies the cleanup service prunes rows whose ExpiresAt is older than the
/// configured retention window and leaves active / recently-expired rows
/// alone. Uses real in-memory SQLite because EF Core InMemory provider does
/// not support <c>ExecuteDeleteAsync</c>.
public class RefreshTokenCleanupServiceTests : IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly AppDbContext _db;

    public RefreshTokenCleanupServiceTests()
    {
        _conn = new SqliteConnection("DataSource=:memory:");
        _conn.Open();
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_conn)
            .Options);
        _db.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _db.Dispose();
        _conn.Dispose();
    }

    [Fact]
    public async Task PruneExpiredAsync_RemovesOnlyRowsBeforeCutoff()
    {
        var user = new User
        {
            Id = Guid.NewGuid(), Username = "u", PasswordHash = "x",
            Role = UserRole.User, MaxRating = "PG-13", FirstName = "", LastName = "",
            CreatedAt = DateTime.UtcNow,
        };
        _db.Users.Add(user);

        var now = DateTime.UtcNow;
        var veryOld = NewToken(user.Id, expiresAt: now.AddDays(-60));
        var oneDayPastCutoff = NewToken(user.Id, expiresAt: now.AddDays(-31));
        var recentlyExpired = NewToken(user.Id, expiresAt: now.AddDays(-5));
        var active = NewToken(user.Id, expiresAt: now.AddDays(3));

        _db.RefreshTokens.AddRange(veryOld, oneDayPastCutoff, recentlyExpired, active);
        await _db.SaveChangesAsync();

        var cutoff = now - RefreshTokenCleanupService.RetainAfterExpiry;
        var deleted = await RefreshTokenCleanupService.PruneExpiredAsync(_db, cutoff);

        Assert.Equal(2, deleted); // veryOld + oneDayPastCutoff

        var remainingIds = await _db.RefreshTokens.Select(rt => rt.Id).ToListAsync();
        Assert.Contains(recentlyExpired.Id, remainingIds);
        Assert.Contains(active.Id, remainingIds);
        Assert.DoesNotContain(veryOld.Id, remainingIds);
        Assert.DoesNotContain(oneDayPastCutoff.Id, remainingIds);
    }

    [Fact]
    public async Task PruneExpiredAsync_EmptyTable_ReturnsZero()
    {
        var deleted = await RefreshTokenCleanupService.PruneExpiredAsync(
            _db, DateTime.UtcNow - TimeSpan.FromDays(30));
        Assert.Equal(0, deleted);
    }

    [Fact]
    public async Task PruneExpiredAsync_AllRecent_ReturnsZero()
    {
        var user = new User
        {
            Id = Guid.NewGuid(), Username = "u", PasswordHash = "x",
            Role = UserRole.User, MaxRating = "PG-13", FirstName = "", LastName = "",
            CreatedAt = DateTime.UtcNow,
        };
        _db.Users.Add(user);
        _db.RefreshTokens.Add(NewToken(user.Id, expiresAt: DateTime.UtcNow.AddDays(7)));
        await _db.SaveChangesAsync();

        var deleted = await RefreshTokenCleanupService.PruneExpiredAsync(
            _db, DateTime.UtcNow - RefreshTokenCleanupService.RetainAfterExpiry);
        Assert.Equal(0, deleted);
    }

    [Fact]
    public void RetainAfterExpiry_Is30Days()
    {
        // Pin the retention window so any deliberate future change requires
        // updating both the code and this test together.
        Assert.Equal(TimeSpan.FromDays(30), RefreshTokenCleanupService.RetainAfterExpiry);
    }

    private static RefreshToken NewToken(Guid userId, DateTime expiresAt) => new()
    {
        Id = Guid.NewGuid(),
        UserId = userId,
        TokenHash = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N"),
        ExpiresAt = expiresAt,
        CreatedAt = expiresAt.AddDays(-7),
    };
}
