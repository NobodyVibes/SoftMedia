using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using OtpNet;
using SoftMedia.Server.Models;
using SoftMedia.Server.Services.Identity;
using SoftMedia.Server.Tests.Helpers;
using Xunit;

namespace SoftMedia.Server.Tests.Integration;

/// Admin recovery: an admin can clear a user's 2FA enrollment via
/// POST /api/v1/admin/users/{id}/disable-2fa. The endpoint is the only out-of-band reset
/// path and MUST be admin-only — non-admins and anonymous callers are rejected, and the
/// target user's 2FA stays intact when a non-admin attempts it.
public class AdminDisableTwoFactorIntegrationTests : IntegrationTestBase
{
    private record EnrollResp(string secret, string otpAuthUri);
    private record ConfirmResp(List<string> recoveryCodes);
    private record TwoFactorRequired(string status, string challengeId);
    private record AuthResp(string accessToken);
    private record UserRow(Guid Id, string Username, bool TwoFactorEnabled);

    private HttpClient JwtClient(User user)
    {
        var client = Factory.CreateClient();
        using var scope = Factory.Services.CreateScope();
        var tokenService = scope.ServiceProvider.GetRequiredService<ITokenService>();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", tokenService.GenerateAccessToken(user));
        return client;
    }

    private static string CodeFor(string base32Secret)
        => new Totp(Base32Encoding.ToBytes(base32Secret)).ComputeTotp();

    private async Task<string> EnrollAndConfirm(HttpClient client)
    {
        var enroll = await client.PostAsync("/api/v1/account/totp/enroll", null);
        enroll.EnsureSuccessStatusCode();
        var e = await enroll.Content.ReadFromJsonAsync<EnrollResp>();
        var confirm = await client.PostAsJsonAsync("/api/v1/account/totp/enroll/confirm", new { code = CodeFor(e!.secret) });
        confirm.EnsureSuccessStatusCode();
        return e.secret;
    }

    /// True when a password-only login is met with a 2FA challenge (i.e. 2FA is active).
    private async Task<bool> LoginIsChallenged(string username, string password = "TestPass!1")
    {
        var anon = Factory.CreateClient();
        var login = await anon.PostAsJsonAsync("/api/v1/auth/login", new { username, password });
        login.EnsureSuccessStatusCode();
        var challenge = await login.Content.ReadFromJsonAsync<TwoFactorRequired>();
        return challenge?.status == "2fa_required";
    }

    [Fact]
    public async Task Admin_CanDisable_UsersTwoFactor()
    {
        var victim = await Factory.SeedUserAsync("disable-victim", password: "TestPass!1");
        await EnrollAndConfirm(JwtClient(victim));
        Assert.True(await LoginIsChallenged("disable-victim")); // 2FA active to begin with

        var admin = await Factory.SeedUserAsync("disable-admin", role: UserRole.Admin);
        var resp = await JwtClient(admin).PostAsync($"/api/v1/admin/users/{victim.Id}/disable-2fa", null);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.False(await LoginIsChallenged("disable-victim")); // 2FA cleared — plain login now works
    }

    [Fact]
    public async Task NonAdmin_CannotDisable_TwoFactor_AndItStaysActive()
    {
        var victim = await Factory.SeedUserAsync("attack-victim", password: "TestPass!1");
        await EnrollAndConfirm(JwtClient(victim));

        var attacker = await Factory.SeedUserAsync("attacker", role: UserRole.User);
        var resp = await JwtClient(attacker).PostAsync($"/api/v1/admin/users/{victim.Id}/disable-2fa", null);

        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);     // authenticated but not Admin
        Assert.True(await LoginIsChallenged("attack-victim"));        // victim's 2FA untouched
    }

    [Fact]
    public async Task Anonymous_CannotDisable_TwoFactor()
    {
        var victim = await Factory.SeedUserAsync("anon-victim", password: "TestPass!1");
        await EnrollAndConfirm(JwtClient(victim));

        var anon = Factory.CreateClient();
        var resp = await anon.PostAsync($"/api/v1/admin/users/{victim.Id}/disable-2fa", null);

        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        Assert.True(await LoginIsChallenged("anon-victim"));
    }

    [Fact]
    public async Task Admin_CannotDisable_OwnTwoFactor_ViaAdminEndpoint()
    {
        // Self-removal must require a password (the self-service flow); the no-password admin
        // recovery endpoint must refuse to act on the caller's own account.
        var admin = await Factory.SeedUserAsync("self-admin", password: "TestPass!1", role: UserRole.Admin);
        await EnrollAndConfirm(JwtClient(admin));

        var resp = await JwtClient(admin).PostAsync($"/api/v1/admin/users/{admin.Id}/disable-2fa", null);

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.True(await LoginIsChallenged("self-admin")); // own 2FA still active
    }

    [Fact]
    public async Task Admin_Disable_WhenNoEnrollment_Returns404()
    {
        var user = await Factory.SeedUserAsync("no-2fa-user");
        var admin = await Factory.SeedUserAsync("admin-404", role: UserRole.Admin);

        var resp = await JwtClient(admin).PostAsync($"/api/v1/admin/users/{user.Id}/disable-2fa", null);
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task UserList_ReflectsTwoFactorEnabled_Flag()
    {
        var user = await Factory.SeedUserAsync("flag-user", password: "TestPass!1");
        var admin = await Factory.SeedUserAsync("flag-admin", role: UserRole.Admin);
        var adminClient = JwtClient(admin);

        var before = await adminClient.GetFromJsonAsync<List<UserRow>>("/api/v1/users");
        Assert.False(before!.Single(u => u.Username == "flag-user").TwoFactorEnabled);

        await EnrollAndConfirm(JwtClient(user));

        var after = await adminClient.GetFromJsonAsync<List<UserRow>>("/api/v1/users");
        Assert.True(after!.Single(u => u.Username == "flag-user").TwoFactorEnabled);
    }
}
