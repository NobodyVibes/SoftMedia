using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using SoftMedia.Server.Models;
using SoftMedia.Server.Services.Identity;
using SoftMedia.Server.Tests.Helpers;
using Xunit;

namespace SoftMedia.Server.Tests.Integration;

/// R-WI-011 end-to-end: admin-created users are unrestricted unless the admin picks ceilings in
/// the create call; invalid labels 400; and the account content-limits endpoint reports the
/// EFFECTIVE ceilings (same logic enforcement uses) for self-display.
public class UserContentRatingsIntegrationTests : IntegrationTestBase
{
    private record ContentLimitsDto(string? Movie, string? Tv, string? Game, bool IsAdmin);

    private HttpClient ClientFor(User user)
    {
        var client = Factory.CreateClient();
        using var scope = Factory.Services.CreateScope();
        var tokenService = scope.ServiceProvider.GetRequiredService<ITokenService>();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", tokenService.GenerateAccessToken(user));
        return client;
    }

    private async Task<HttpClient> AdminClientAsync()
        => ClientFor(await Factory.SeedUserAsync("ratingsadmin", role: UserRole.Admin));

    [Fact]
    public async Task CreateUser_WithoutRatings_IsUnrestricted()
    {
        var admin = await AdminClientAsync();

        var resp = await admin.PostAsJsonAsync("/api/v1/users", new
        {
            username = "freeuser", password = "Password1!", role = "User",
            firstName = "Free", lastName = "User",
        });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var body = await resp.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.Equal("", body.GetProperty("maxRating").GetString()); // never capped by default
        Assert.Equal(0, body.GetProperty("contentRatings").EnumerateObject().Count());
    }

    [Fact]
    public async Task CreateUser_WithRatings_StoresThem_AndSyncsLegacyMovie()
    {
        var admin = await AdminClientAsync();

        var resp = await admin.PostAsJsonAsync("/api/v1/users", new
        {
            username = "kiduser", password = "Password1!", role = "User",
            firstName = "Kid", lastName = "User",
            contentRatings = new Dictionary<string, string> { ["Movie"] = "PG", ["TV"] = "TV-Y7" },
        });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var body = await resp.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.Equal("PG", body.GetProperty("maxRating").GetString());
        Assert.Equal("PG", body.GetProperty("contentRatings").GetProperty("Movie").GetString());
        Assert.Equal("TV-Y7", body.GetProperty("contentRatings").GetProperty("TV").GetString());
    }

    [Fact]
    public async Task CreateUser_WithInvalidRating_Returns400()
    {
        var admin = await AdminClientAsync();

        var resp = await admin.PostAsJsonAsync("/api/v1/users", new
        {
            username = "badrating", password = "Password1!", role = "User",
            firstName = "Bad", lastName = "Rating",
            contentRatings = new Dictionary<string, string> { ["Movie"] = "BANANA" },
        });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Contains("Unknown Movie rating", await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ContentLimits_ReflectEffectiveCeilings_ForUserAndAdmin()
    {
        // Restricted non-admin sees their ceilings; the admin sees isAdmin (no limits).
        var restricted = await Factory.SeedUserAsync("cappeduser");
        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SoftMedia.Server.Data.AppDbContext>();
            var row = await db.Users.FindAsync(restricted.Id);
            row!.ContentRatings = "{\"Movie\":\"PG-13\",\"TV\":\"TV-PG\"}";
            row.MaxRating = "PG-13";
            await db.SaveChangesAsync();
        }

        var userLimits = await ClientFor(restricted)
            .GetFromJsonAsync<ContentLimitsDto>("/api/v1/account/content-limits");
        Assert.NotNull(userLimits);
        Assert.False(userLimits!.IsAdmin);
        Assert.Equal("PG-13", userLimits.Movie);
        Assert.Equal("TV-PG", userLimits.Tv);
        Assert.Null(userLimits.Game);

        var adminLimits = await (await AdminClientAsync())
            .GetFromJsonAsync<ContentLimitsDto>("/api/v1/account/content-limits");
        Assert.True(adminLimits!.IsAdmin);
        Assert.Null(adminLimits.Movie);
    }

    [Fact]
    public async Task ContentLimits_LegacyMaxRatingFallback_IsVisible()
    {
        // A pre-decision user still carrying MaxRating="PG-13" with an empty map: the display
        // endpoint must surface that hidden movie cap (it is enforced!), not claim "no limits".
        var legacy = await Factory.SeedUserAsync("legacyuser");
        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SoftMedia.Server.Data.AppDbContext>();
            var row = await db.Users.FindAsync(legacy.Id);
            row!.MaxRating = "PG-13";
            row.ContentRatings = "{}";
            await db.SaveChangesAsync();
        }

        var limits = await ClientFor(legacy)
            .GetFromJsonAsync<ContentLimitsDto>("/api/v1/account/content-limits");
        Assert.Equal("PG-13", limits!.Movie); // the invisible default, finally visible
    }
}
