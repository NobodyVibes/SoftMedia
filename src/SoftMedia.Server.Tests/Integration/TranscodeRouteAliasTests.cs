using System.Net;
using Microsoft.Extensions.DependencyInjection;
using SoftMedia.Server.Services.Identity;
using SoftMedia.Server.Tests.Helpers;
using Xunit;

namespace SoftMedia.Server.Tests.Integration;

/// NR-WI-004 — `api/v1/transcode` is the canonical route; `api/transcode` remains a
/// deprecated alias. Both prefixes must resolve to the SAME controller with the SAME
/// auth and token rules — the audits' recurring failure mode is enforcement applied
/// at most but not all entry points, so every rule is asserted on BOTH prefixes.
public class TranscodeRouteAliasTests : IntegrationTestBase
{
    public static readonly TheoryData<string> Prefixes = new() { "/api/v1/transcode", "/api/transcode" };

    private async Task<(string MediaToken, string AccessToken)> SeedTokensAsync(string name)
    {
        var user = await Factory.SeedUserAsync(name);
        using var scope = Factory.Services.CreateScope();
        var tokens = scope.ServiceProvider.GetRequiredService<ITokenService>();
        return (tokens.GenerateMediaToken(user).Token, tokens.GenerateAccessToken(user));
    }

    [Theory]
    [MemberData(nameof(Prefixes))]
    public async Task Anonymous_Is401_OnBothPrefixes(string prefix)
    {
        var resp = await Factory.CreateClient().GetAsync($"{prefix}/{Guid.NewGuid()}/master.m3u8");
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Theory]
    [MemberData(nameof(Prefixes))]
    public async Task MediaTokenInQuery_Authenticates_OnBothPrefixes(string prefix)
    {
        var (mediaToken, _) = await SeedTokensAsync($"alias-media-{prefix.GetHashCode():x}");

        // Unknown media id: 404 proves the request was routed AND authenticated
        // (an auth failure would be 401 before the media lookup).
        var resp = await Factory.CreateClient()
            .GetAsync($"{prefix}/{Guid.NewGuid()}/master.m3u8?token={mediaToken}");

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Theory]
    [MemberData(nameof(Prefixes))]
    public async Task FullAccessTokenInQuery_IsRejected_OnBothPrefixes(string prefix)
    {
        // WS-6 T6.1: full access tokens must never authenticate from the query string.
        // The alias must enforce this identically to the canonical route.
        var (_, accessToken) = await SeedTokensAsync($"alias-access-{prefix.GetHashCode():x}");

        var resp = await Factory.CreateClient()
            .GetAsync($"{prefix}/{Guid.NewGuid()}/master.m3u8?token={accessToken}");

        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }
}
