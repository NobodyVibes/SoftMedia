using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SoftMedia.Server.Models;
using SoftMedia.Server.Services.Identity;
using SoftMedia.Server.Tests.Helpers;
using Xunit;

namespace SoftMedia.Server.Tests.Integration;

/// Session E API contracts:
///   • SR-WI-060 — every DateTime the API serializes carries an explicit UTC marker
///     ("Z" or an offset), including entity-sourced values SQLite round-trips with
///     Kind=Unspecified (previously emitted bare and parsed as LOCAL time by JS).
///   • SR-WI-061 — one error envelope: unhandled exceptions, [ApiController] validation
///     failures, migrated manual 4xx returns, and the must-change-password middleware
///     all speak RFC 7807 application/problem+json (with traceId; discriminators as
///     extensions; no stack traces).
public class ApiContractIntegrationTests : IntegrationTestBase
{
    private HttpClient ClientFor(User user)
    {
        var client = Factory.CreateClient();
        using var scope = Factory.Services.CreateScope();
        var tokenService = scope.ServiceProvider.GetRequiredService<ITokenService>();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", tokenService.GenerateAccessToken(user));
        return client;
    }

    // ---------- SR-WI-060 — UTC markers on serialized DateTimes ----------

    /// Any ISO-like date-time string in a JSON payload. The tail captures whatever
    /// follows the seconds so the assertion can demand a Z or an explicit offset.
    private static readonly Regex DateTimeLike = new(
        "\"(?<value>\\d{4}-\\d{2}-\\d{2}T\\d{2}:\\d{2}:\\d{2}(?:\\.\\d+)?(?<suffix>[^\"]*))\"",
        RegexOptions.Compiled);

    private static void AssertAllDateTimesCarryUtcMarker(string json, string endpoint)
    {
        var matches = DateTimeLike.Matches(json);
        Assert.True(matches.Count > 0, $"{endpoint}: expected at least one DateTime in the payload");
        foreach (Match m in matches)
        {
            var suffix = m.Groups["suffix"].Value;
            Assert.True(
                suffix == "Z" || Regex.IsMatch(suffix, @"^[+-]\d{2}:\d{2}$"),
                $"{endpoint}: DateTime '{m.Groups["value"].Value}' lacks a Z/offset marker (SR-WI-060)");
        }
    }

    [Fact]
    public async Task RecentMedia_EntitySourcedDateTimes_SerializeWithUtcMarker()
    {
        var user = await Factory.SeedUserAsync("utc-user");
        await Factory.WithDbAsync(async db =>
        {
            var lib = new Library { Id = Guid.NewGuid(), Name = "Utc-Lib", Type = LibraryType.Movie, Paths = new() { "/utc" } };
            db.Libraries.Add(lib);
            db.MediaItems.Add(new MediaItem
            {
                Id = Guid.NewGuid(),
                LibraryId = lib.Id,
                Title = "Utc Movie",
                SortTitle = "Utc Movie",
                Path = "/utc/movie.mkv",
                Type = MediaType.Movie,
                Duration = 5400,
                // The SQLite reality this item exists for: stored-UTC values come back
                // with Kind=Unspecified. The converter must stamp, not shift.
                DateAdded = DateTime.SpecifyKind(new DateTime(2026, 7, 20, 9, 15, 30), DateTimeKind.Unspecified),
            });
            await db.SaveChangesAsync();
        });

        var client = ClientFor(user);
        var resp = await client.GetAsync("/api/v1/media/recent?limit=5");
        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadAsStringAsync();

        Assert.Contains("Utc Movie", json);
        // The seeded Unspecified instant must appear verbatim (stamped, not shifted).
        Assert.Contains("2026-07-20T09:15:30", json);
        AssertAllDateTimesCarryUtcMarker(json, "/api/v1/media/recent");
    }

    // ---------- SR-WI-061 — RFC 7807 everywhere ----------

    private static async Task<JsonElement> AssertProblemShapeAsync(
        HttpResponseMessage resp, int expectedStatus, bool expectTraceId = true)
    {
        Assert.Equal(expectedStatus, (int)resp.StatusCode);
        Assert.Equal("application/problem+json", resp.Content.Headers.ContentType?.MediaType);
        var body = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement;
        Assert.True(body.TryGetProperty("title", out _), "problem body must carry a title");
        Assert.Equal(expectedStatus, body.GetProperty("status").GetInt32());
        if (expectTraceId)
        {
            Assert.True(body.TryGetProperty("traceId", out _), "problem body must carry a traceId");
        }
        return body;
    }

    [Fact]
    public async Task UnhandledException_ReturnsProblemDetails_WithoutStackTrace()
    {
        var client = Factory.CreateClient();
        var resp = await client.GetAsync("/api/v1/debug/throw");
        var body = await AssertProblemShapeAsync(resp, 500);

        // No stack traces and no exception internals in ANY environment.
        var raw = body.GetRawText();
        Assert.DoesNotContain("InvalidOperationException", raw);
        Assert.DoesNotContain("at SoftMedia", raw);
        Assert.DoesNotContain("Deliberate unhandled exception", raw);
    }

    [Fact]
    public async Task ValidationFailure_ReturnsProblemDetails()
    {
        var user = await Factory.SeedUserAsync("val-user");
        var client = ClientFor(user);

        // int-binding failure on [ApiController] → automatic ValidationProblemDetails.
        var resp = await client.GetAsync("/api/v1/media/recent?limit=not-a-number");
        var body = await AssertProblemShapeAsync(resp, 400);
        Assert.True(body.TryGetProperty("errors", out var errors), "validation problem must carry errors");
        Assert.True(errors.EnumerateObject().Any());
    }

    [Fact]
    public async Task MigratedManual404_ReturnsProblemDetails_WithPreservedMessage()
    {
        var user = await Factory.SeedUserAsync("manual-404-user");
        var client = ClientFor(user);

        var resp = await client.GetAsync($"/api/v1/episode/{Guid.NewGuid()}/next");
        var body = await AssertProblemShapeAsync(resp, 404);
        // Message text preserved from the pre-7807 { message } body.
        Assert.Equal("Episode not found or no next episode", body.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task MustChangePasswordMiddleware_EmitsProblemDetails_WithErrorExtension()
    {
        var user = await Factory.SeedUserAsync("pd-changeme");
        await Factory.WithDbAsync(async db =>
        {
            var u = await db.Users.FirstAsync(x => x.Id == user.Id);
            u.MustChangePassword = true;
            await db.SaveChangesAsync();
        });

        var client = Factory.CreateClient();
        var login = await client.PostAsJsonAsync("/api/v1/auth/login",
            new { Username = "pd-changeme", Password = "TestPass!1" });
        login.EnsureSuccessStatusCode();
        var token = JsonDocument.Parse(await login.Content.ReadAsStringAsync())
            .RootElement.GetProperty("accessToken").GetString();

        var req = new HttpRequestMessage(HttpMethod.Get, "/api/v1/media/hero");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var blocked = await client.SendAsync(req);

        var body = await AssertProblemShapeAsync(blocked, 403);
        // The machine-read discriminator survives as an extension member.
        Assert.Equal("password_change_required", body.GetProperty("error").GetString());
        Assert.Equal("You must change your password before continuing.",
            body.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task MigratedAdminConflict_ReturnsProblemDetails()
    {
        var admin = await Factory.SeedUserAsync("pd-admin", role: UserRole.Admin);
        var itemId = await Factory.WithDbAsync(async db =>
        {
            var lib = new Library { Id = Guid.NewGuid(), Name = "PD-Lib", Type = LibraryType.Movie, Paths = new() { "/pd" } };
            db.Libraries.Add(lib);
            var item = new MediaItem
            {
                Id = Guid.NewGuid(),
                LibraryId = lib.Id,
                Title = "Locked Movie",
                SortTitle = "Locked Movie",
                Path = "/pd/locked.mkv",
                Type = MediaType.Movie,
                MetadataLocked = true,
            };
            db.MediaItems.Add(item);
            await db.SaveChangesAsync();
            return item.Id;
        });

        var client = ClientFor(admin);
        var resp = await client.PostAsync($"/api/v1/admin/match/{itemId}/refresh", content: null);
        var body = await AssertProblemShapeAsync(resp, 409);
        Assert.Equal("Metadata is locked for this item. Unlock it first to refresh.",
            body.GetProperty("detail").GetString());
    }
}
