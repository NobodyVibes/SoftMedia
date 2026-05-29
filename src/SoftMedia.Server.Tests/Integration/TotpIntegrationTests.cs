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

/// End-to-end TOTP 2FA (P2-WI-005): enroll → confirm → login-with-challenge,
/// recovery codes, wrong-code rejection, and disable.
public class TotpIntegrationTests : IntegrationTestBase
{
    private record EnrollResp(string secret, string otpAuthUri);
    private record ConfirmResp(List<string> recoveryCodes);
    private record TwoFactorRequired(string status, string challengeId);
    private record AuthResp(string accessToken);

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

    private async Task<(string secret, List<string> recovery)> EnrollAndConfirm(HttpClient client)
    {
        var enroll = await client.PostAsync("/api/v1/account/totp/enroll", null);
        enroll.EnsureSuccessStatusCode();
        var e = await enroll.Content.ReadFromJsonAsync<EnrollResp>();

        var confirm = await client.PostAsJsonAsync("/api/v1/account/totp/enroll/confirm", new { code = CodeFor(e!.secret) });
        confirm.EnsureSuccessStatusCode();
        var c = await confirm.Content.ReadFromJsonAsync<ConfirmResp>();
        return (e.secret, c!.recoveryCodes);
    }

    [Fact]
    public async Task Enroll_Confirm_EnablesTwoFactor_AndReturnsRecoveryCodes()
    {
        var user = await Factory.SeedUserAsync("totp1", password: "TestPass!1");
        var client = JwtClient(user);

        var (_, recovery) = await EnrollAndConfirm(client);
        Assert.Equal(10, recovery.Count);

        var status = await client.GetFromJsonAsync<Dictionary<string, object>>("/api/v1/account/totp");
        Assert.True(((System.Text.Json.JsonElement)status!["enabled"]).GetBoolean());
    }

    [Fact]
    public async Task Login_WithTwoFactorEnabled_ReturnsChallenge_ThenCompletes()
    {
        var user = await Factory.SeedUserAsync("totp2", password: "TestPass!1");
        var (secret, _) = await EnrollAndConfirm(JwtClient(user));

        var anon = Factory.CreateClient();
        var login = await anon.PostAsJsonAsync("/api/v1/auth/login", new { username = "totp2", password = "TestPass!1" });
        login.EnsureSuccessStatusCode();
        var challenge = await login.Content.ReadFromJsonAsync<TwoFactorRequired>();
        Assert.Equal("2fa_required", challenge!.status);
        Assert.False(string.IsNullOrEmpty(challenge.challengeId));

        var complete = await anon.PostAsJsonAsync(
            $"/api/v1/auth/2fa?challengeId={challenge.challengeId}",
            new { challengeId = challenge.challengeId, code = CodeFor(secret) });
        complete.EnsureSuccessStatusCode();
        var auth = await complete.Content.ReadFromJsonAsync<AuthResp>();
        Assert.False(string.IsNullOrEmpty(auth!.accessToken));
    }

    [Fact]
    public async Task TwoFactor_WrongCode_IsRejected()
    {
        var user = await Factory.SeedUserAsync("totp3", password: "TestPass!1");
        await EnrollAndConfirm(JwtClient(user));

        var anon = Factory.CreateClient();
        var login = await anon.PostAsJsonAsync("/api/v1/auth/login", new { username = "totp3", password = "TestPass!1" });
        var challenge = await login.Content.ReadFromJsonAsync<TwoFactorRequired>();

        var complete = await anon.PostAsJsonAsync(
            $"/api/v1/auth/2fa?challengeId={challenge!.challengeId}",
            new { challengeId = challenge.challengeId, code = "000000" });
        Assert.Equal(HttpStatusCode.Unauthorized, complete.StatusCode);
    }

    [Fact]
    public async Task RecoveryCode_CompletesLogin_AndIsSingleUse()
    {
        var user = await Factory.SeedUserAsync("totp4", password: "TestPass!1");
        var (_, recovery) = await EnrollAndConfirm(JwtClient(user));
        var code = recovery[0];

        // First use succeeds.
        var anon = Factory.CreateClient();
        var login1 = await anon.PostAsJsonAsync("/api/v1/auth/login", new { username = "totp4", password = "TestPass!1" });
        var ch1 = await login1.Content.ReadFromJsonAsync<TwoFactorRequired>();
        var ok = await anon.PostAsJsonAsync($"/api/v1/auth/2fa?challengeId={ch1!.challengeId}",
            new { challengeId = ch1.challengeId, code });
        ok.EnsureSuccessStatusCode();

        // Second use of the same recovery code fails.
        var anon2 = Factory.CreateClient();
        var login2 = await anon2.PostAsJsonAsync("/api/v1/auth/login", new { username = "totp4", password = "TestPass!1" });
        var ch2 = await login2.Content.ReadFromJsonAsync<TwoFactorRequired>();
        var reuse = await anon2.PostAsJsonAsync($"/api/v1/auth/2fa?challengeId={ch2!.challengeId}",
            new { challengeId = ch2.challengeId, code });
        Assert.Equal(HttpStatusCode.Unauthorized, reuse.StatusCode);
    }

    [Fact]
    public async Task Disable_RemovesTwoFactor_LoginNoLongerChallenges()
    {
        var user = await Factory.SeedUserAsync("totp5", password: "TestPass!1");
        var client = JwtClient(user);
        var (secret, _) = await EnrollAndConfirm(client);

        var disable = await client.PostAsJsonAsync("/api/v1/account/totp/disable",
            new { password = "TestPass!1", code = CodeFor(secret) });
        disable.EnsureSuccessStatusCode();

        var anon = Factory.CreateClient();
        var login = await anon.PostAsJsonAsync("/api/v1/auth/login", new { username = "totp5", password = "TestPass!1" });
        login.EnsureSuccessStatusCode();
        // No longer a challenge — a normal AuthResponse with an access token.
        var auth = await login.Content.ReadFromJsonAsync<AuthResp>();
        Assert.False(string.IsNullOrEmpty(auth!.accessToken));
    }
}
