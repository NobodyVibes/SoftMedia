using Microsoft.EntityFrameworkCore;
using SoftMedia.Server.Data;
using SoftMedia.Server.Models;
using SoftMedia.Server.Services.Abstractions;
using SoftMedia.Server.Services.Identity;
using Xunit;

namespace SoftMedia.Server.Tests.Services.Identity;

public class RefreshTokenServiceTests
{
    private static AppDbContext NewDb() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"refresh-token-tests-{Guid.NewGuid()}")
            .Options);

    private static User NewUser() => new()
    {
        Id = Guid.NewGuid(),
        Username = "tester",
        PasswordHash = "unused-in-these-tests",
        Role = UserRole.User,
        MaxRating = "PG-13",
        CreatedAt = DateTime.UtcNow,
        IsApproved = true,
        FirstName = "T", LastName = "T"
    };

    [Fact]
    public async Task IssueAsync_ReturnsRawToken_AndPersistsHashOnly()
    {
        using var db = NewDb();
        var user = NewUser();
        db.Users.Add(user);
        await db.SaveChangesAsync();

        var svc = new RefreshTokenService(db);
        var (raw, entity) = await svc.IssueAsync(user, "127.0.0.1");

        Assert.NotEqual(raw, entity.TokenHash);
        Assert.Equal(64, entity.TokenHash.Length); // SHA-256 hex = 64 chars
        Assert.False(db.RefreshTokens.Any(rt => rt.TokenHash == raw),
            "raw token must never be persisted");
    }

    [Fact]
    public async Task ValidateAsync_ValidToken_ReturnsOk()
    {
        using var db = NewDb();
        var user = NewUser(); db.Users.Add(user); await db.SaveChangesAsync();
        var svc = new RefreshTokenService(db);

        var (raw, _) = await svc.IssueAsync(user, null);
        var result = await svc.ValidateAsync(raw);

        Assert.True(result.IsValid);
        Assert.False(result.IsReuse);
        Assert.NotNull(result.Token);
    }

    [Fact]
    public async Task ValidateAsync_UnknownToken_ReturnsNotFound()
    {
        using var db = NewDb();
        var svc = new RefreshTokenService(db);
        var result = await svc.ValidateAsync("not-a-real-token");

        Assert.False(result.IsValid);
        Assert.False(result.IsReuse);
        Assert.Null(result.Token);
    }

    [Fact]
    public async Task ValidateAsync_EmptyOrWhitespace_ReturnsNotFound()
    {
        using var db = NewDb();
        var svc = new RefreshTokenService(db);

        Assert.False((await svc.ValidateAsync("")).IsValid);
        Assert.False((await svc.ValidateAsync("   ")).IsValid);
    }

    [Fact]
    public async Task ValidateAsync_ExpiredToken_ReturnsInvalid_NotReuse()
    {
        using var db = NewDb();
        var user = NewUser(); db.Users.Add(user); await db.SaveChangesAsync();
        var clock = new StubTimeProvider(DateTimeOffset.UtcNow);
        var svc = new RefreshTokenService(db, clock);

        var (raw, _) = await svc.IssueAsync(user, null);
        clock.Advance(RefreshTokenService.DefaultLifetime + TimeSpan.FromMinutes(1));

        var result = await svc.ValidateAsync(raw);
        Assert.False(result.IsValid);
        Assert.False(result.IsReuse);
    }

    [Fact]
    public async Task ValidateAsync_RevokedButNotReplaced_ReturnsInvalid_NotReuse()
    {
        using var db = NewDb();
        var user = NewUser(); db.Users.Add(user); await db.SaveChangesAsync();
        var svc = new RefreshTokenService(db);

        var (raw, entity) = await svc.IssueAsync(user, null);
        await svc.RevokeAsync(entity, RefreshTokenRevocationReason.Logout, null);

        var result = await svc.ValidateAsync(raw);
        Assert.False(result.IsValid);
        Assert.False(result.IsReuse);
    }

    [Fact]
    public async Task ValidateAsync_RevokedAndReplaced_ReturnsReuseDetected()
    {
        using var db = NewDb();
        var user = NewUser(); db.Users.Add(user); await db.SaveChangesAsync();
        var svc = new RefreshTokenService(db);

        var (oldRaw, oldEntity) = await svc.IssueAsync(user, null);
        await svc.RotateAsync(oldEntity, null);

        // Present the old (rotated-away) raw token
        var result = await svc.ValidateAsync(oldRaw);

        Assert.False(result.IsValid);
        Assert.True(result.IsReuse);
        Assert.NotNull(result.Token);
    }

    [Fact]
    public async Task RotateAsync_MarksOldAsRotated_AndLinksReplacement()
    {
        using var db = NewDb();
        var user = NewUser(); db.Users.Add(user); await db.SaveChangesAsync();
        var svc = new RefreshTokenService(db);

        var (_, oldEntity) = await svc.IssueAsync(user, null);
        var rotated = await svc.RotateAsync(oldEntity, "10.0.0.1");
        Assert.NotNull(rotated);
        var (newRaw, newEntity) = rotated!.Value;

        var refreshedOld = await db.RefreshTokens.FindAsync(oldEntity.Id);
        Assert.NotNull(refreshedOld!.RevokedAt);
        Assert.Equal(RefreshTokenRevocationReason.Rotated, refreshedOld.ReasonRevoked);
        Assert.Equal(newEntity.Id, refreshedOld.ReplacedByTokenId);

        // New one is active and different
        Assert.NotEqual(oldEntity.Id, newEntity.Id);
        Assert.Null(newEntity.RevokedAt);
        Assert.NotEqual(oldEntity.TokenHash, newEntity.TokenHash);

        // Raw returned for new is valid
        var result = await svc.ValidateAsync(newRaw);
        Assert.True(result.IsValid);
    }

    [Fact]
    public async Task RotateAsync_SecondRotationOfSameToken_ReturnsNull_AndDoesNotForkTheChain()
    {
        // Audit wave-2 I-5: rotating the SAME token twice (the concurrent-refresh case) must claim
        // it atomically — the second rotation returns null instead of creating a second live
        // replacement that would fork the chain and defeat reuse-detection.
        using var db = NewDb();
        var user = NewUser(); db.Users.Add(user); await db.SaveChangesAsync();
        var svc = new RefreshTokenService(db);

        var (_, oldEntity) = await svc.IssueAsync(user, null);

        var first = await svc.RotateAsync(oldEntity, null);
        Assert.NotNull(first); // first rotation wins

        var second = await svc.RotateAsync(oldEntity, null);
        Assert.Null(second);   // loser gets null, no second replacement

        // old (now revoked) + exactly one replacement = 2 rows. No fork.
        Assert.Equal(2, db.RefreshTokens.Count());
    }

    [Fact]
    public async Task RevokeAsync_Idempotent()
    {
        using var db = NewDb();
        var user = NewUser(); db.Users.Add(user); await db.SaveChangesAsync();
        var svc = new RefreshTokenService(db);

        var (_, entity) = await svc.IssueAsync(user, null);
        await svc.RevokeAsync(entity, RefreshTokenRevocationReason.Logout, null);
        var firstRevokedAt = entity.RevokedAt;

        await svc.RevokeAsync(entity, RefreshTokenRevocationReason.Logout, null);
        Assert.Equal(firstRevokedAt, entity.RevokedAt);
    }

    [Fact]
    public async Task RevokeAllForUserAsync_OnlyRevokesActiveTokens_ForThatUser()
    {
        using var db = NewDb();
        var userA = NewUser(); var userB = NewUser();
        db.Users.AddRange(userA, userB);
        await db.SaveChangesAsync();

        var svc = new RefreshTokenService(db);
        var (_, a1) = await svc.IssueAsync(userA, null);
        var (_, a2) = await svc.IssueAsync(userA, null);
        var (_, b1) = await svc.IssueAsync(userB, null);

        // Revoke one of A's manually so we can verify it's left alone
        await svc.RevokeAsync(a1, RefreshTokenRevocationReason.Logout, null);
        var a1RevokedAt = a1.RevokedAt;

        await svc.RevokeAllForUserAsync(userA.Id, RefreshTokenRevocationReason.PasswordChange);

        Assert.Equal(a1RevokedAt, a1.RevokedAt); // unchanged — was already revoked
        Assert.Equal(RefreshTokenRevocationReason.Logout, a1.ReasonRevoked);

        Assert.NotNull(a2.RevokedAt);
        Assert.Equal(RefreshTokenRevocationReason.PasswordChange, a2.ReasonRevoked);

        Assert.Null(b1.RevokedAt); // user B untouched
    }

    [Fact]
    public async Task IssueAsync_ProducesDistinctTokens()
    {
        using var db = NewDb();
        var user = NewUser(); db.Users.Add(user); await db.SaveChangesAsync();
        var svc = new RefreshTokenService(db);

        var (raw1, _) = await svc.IssueAsync(user, null);
        var (raw2, _) = await svc.IssueAsync(user, null);

        Assert.NotEqual(raw1, raw2);
    }

    private class StubTimeProvider : TimeProvider
    {
        private DateTimeOffset _now;
        public StubTimeProvider(DateTimeOffset now) => _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now = _now.Add(by);
    }
}
