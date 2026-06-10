using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using SoftMedia.Server.Data;
using SoftMedia.Server.Models;
using SoftMedia.Server.Tests.Helpers;
using Xunit;

namespace SoftMedia.Server.Tests.Integration;

/// Security audit WS-8 (L2 password policy, L11 invite single-use atomicity).
public class AuthHardeningTests : IntegrationTestBase
{
    private async Task SetSettingAsync(string key, string value)
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var existing = await db.Settings.FindAsync(key);
        if (existing != null) existing.Value = value;
        else db.Settings.Add(new AppSetting { Key = key, Value = value });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Signup_ShortPassword_IsRejected_L2()
    {
        await SetSettingAsync("AllowUserSignup", "Enabled");
        var resp = await Factory.CreateClient().PostAsJsonAsync("/api/v1/auth/signup", new
        {
            Username = "shortpw", Password = "abc", InviteCode = (string?)null, FirstName = "A", LastName = "B",
        });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Invite_IsSingleUse_L11()
    {
        // End-to-end single-use property. (The race itself is closed by the atomic conditional
        // UPDATE in Signup — see AuthController; a true-concurrency test is omitted because the
        // shared in-memory-SQLite test harness can't model concurrent writers reliably.)
        await SetSettingAsync("AllowUserSignup", "InviteOnly");
        var creator = await Factory.SeedUserAsync("inviter", role: UserRole.Admin);
        var code = "INV-" + Guid.NewGuid().ToString("N")[..8];
        await Factory.WithDbAsync(async db =>
        {
            db.Invites.Add(new Invite { Id = Guid.NewGuid(), Code = code, CreatedById = creator.Id, CreatedAt = DateTime.UtcNow });
            await db.SaveChangesAsync();
        });

        var first = await Factory.CreateClient().PostAsJsonAsync("/api/v1/auth/signup", new
        {
            Username = "firstuser", Password = "StrongPass!9", InviteCode = code, FirstName = "A", LastName = "B",
        });
        Assert.Equal(HttpStatusCode.OK, first.StatusCode); // invite -> approved -> token issued

        var second = await Factory.CreateClient().PostAsJsonAsync("/api/v1/auth/signup", new
        {
            Username = "seconduser", Password = "StrongPass!9", InviteCode = code, FirstName = "C", LastName = "D",
        });
        Assert.Equal(HttpStatusCode.BadRequest, second.StatusCode); // the invite is already consumed
    }
}
