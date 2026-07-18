using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using SoftMedia.Server.Models;
using SoftMedia.Server.Services.Identity;
using SoftMedia.Server.Services.Scanning;
using SoftMedia.Server.Tests.Helpers;
using Xunit;

namespace SoftMedia.Server.Tests.Integration;

/// R-WI-019 — the *arr scan webhook. Pins: scope gating (write:library, NOT the
/// admin role — the whole point is a least-privilege credential in Sonarr/Radarr
/// config), path-jailing to configured library roots, owning-library mapping,
/// and queue deduplication behaviour.
public class ScanWebhookIntegrationTests : IntegrationTestBase
{
    private HttpClient JwtClient(User user)
    {
        var client = Factory.CreateClient();
        using var scope = Factory.Services.CreateScope();
        var tokenService = scope.ServiceProvider.GetRequiredService<ITokenService>();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", tokenService.GenerateAccessToken(user));
        return client;
    }

    private HttpClient ApiTokenClient(string rawToken)
    {
        var client = Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", rawToken);
        return client;
    }

    private async Task<string> MintTokenAsync(User user, params string[] scopes)
    {
        var resp = await JwtClient(user).PostAsJsonAsync("/api/v1/account/api-tokens",
            new { label = "arr", scopes, expiresAt = (DateTime?)null });
        resp.EnsureSuccessStatusCode();
        var mint = await resp.Content.ReadFromJsonAsync<MintDto>();
        return mint!.token;
    }

    private async Task<(Guid movieLibId, Guid tvLibId)> SeedLibrariesAsync()
    {
        return await Factory.WithDbAsync(async db =>
        {
            var movies = new Library { Id = Guid.NewGuid(), Name = "Webhook-Movies", Type = LibraryType.Movie, Paths = new() { @"D:\WebhookTest\Movies" } };
            var tv = new Library { Id = Guid.NewGuid(), Name = "Webhook-TV", Type = LibraryType.TV, Paths = new() { @"D:\WebhookTest\TV" } };
            db.Libraries.AddRange(movies, tv);
            await db.SaveChangesAsync();
            return (movies.Id, tv.Id);
        });
    }

    [Fact]
    public async Task Anonymous_Is401_AndTokenWithoutTheScope_Is403()
    {
        await SeedLibrariesAsync();
        var user = await Factory.SeedUserAsync("arr-user1");

        var anon = Factory.CreateClient();
        var anonResp = await anon.PostAsJsonAsync("/api/v1/scan", new { path = @"D:\WebhookTest\Movies\Film" });
        Assert.Equal(HttpStatusCode.Unauthorized, anonResp.StatusCode);

        // A write:state token (the broadest non-admin scope before this item) must NOT trigger scans.
        var wrongScope = await MintTokenAsync(user, ApiTokenScopes.WriteState);
        var forbidden = await ApiTokenClient(wrongScope)
            .PostAsJsonAsync("/api/v1/scan", new { path = @"D:\WebhookTest\Movies\Film" });
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
    }

    [Fact]
    public async Task WriteLibraryToken_WithAPathInsideALibrary_EnqueuesThatLibrary()
    {
        var (movieLibId, _) = await SeedLibrariesAsync();
        var user = await Factory.SeedUserAsync("arr-user2");
        var token = await MintTokenAsync(user, ApiTokenScopes.WriteLibrary);

        var resp = await ApiTokenClient(token).PostAsJsonAsync("/api/v1/scan",
            new { path = @"D:\WebhookTest\Movies\Some Film (2024)\film.mkv" });

        Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<ScanDto>();
        Assert.Equal(movieLibId, body!.libraryId);
        Assert.Equal("Webhook-Movies", body.libraryName);

        var queue = Factory.Services.GetRequiredService<ILibraryScanQueueService>();
        Assert.True(queue.IsLibraryInQueue(movieLibId));
    }

    [Fact]
    public async Task ForwardSlashes_AndTheRootItself_ResolveToTheOwningLibrary()
    {
        // *arr tools on Docker/Linux mounts post forward-slash paths.
        var (_, tvLibId) = await SeedLibrariesAsync();
        var user = await Factory.SeedUserAsync("arr-user3");
        var token = await MintTokenAsync(user, ApiTokenScopes.WriteLibrary);
        var client = ApiTokenClient(token);

        var slashes = await client.PostAsJsonAsync("/api/v1/scan",
            new { path = "D:/WebhookTest/TV/Some Show/Season 01" });
        Assert.Equal(HttpStatusCode.Accepted, slashes.StatusCode);
        Assert.Equal(tvLibId, (await slashes.Content.ReadFromJsonAsync<ScanDto>())!.libraryId);

        var rootItself = await client.PostAsJsonAsync("/api/v1/scan",
            new { path = @"D:\WebhookTest\TV" });
        Assert.Equal(HttpStatusCode.Accepted, rootItself.StatusCode);
    }

    [Fact]
    public async Task PathsOutsideEveryLibrary_AreRejected_IncludingPrefixCousinsAndTraversal()
    {
        await SeedLibrariesAsync();
        var user = await Factory.SeedUserAsync("arr-user4");
        var token = await MintTokenAsync(user, ApiTokenScopes.WriteLibrary);
        var client = ApiTokenClient(token);

        foreach (var bad in new[]
        {
            @"C:\Windows\System32",
            @"D:\WebhookTest\MoviesEvil\x.mkv",              // prefix cousin of ...\Movies
            @"D:\WebhookTest\Movies\..\..\secrets\file.mkv", // traversal escapes the root
        })
        {
            var resp = await client.PostAsJsonAsync("/api/v1/scan", new { path = bad });
            Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        }
    }

    [Fact]
    public async Task PathlessCall_EnqueuesEveryLibrary_AndRepeatsDeduplicate()
    {
        var (movieLibId, tvLibId) = await SeedLibrariesAsync();
        var user = await Factory.SeedUserAsync("arr-user5");
        var token = await MintTokenAsync(user, ApiTokenScopes.WriteLibrary);
        var client = ApiTokenClient(token);

        var resp = await client.PostAsJsonAsync("/api/v1/scan", new { });
        Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);

        var queue = Factory.Services.GetRequiredService<ILibraryScanQueueService>();
        Assert.True(queue.IsLibraryInQueue(movieLibId));
        Assert.True(queue.IsLibraryInQueue(tvLibId));

        // Second ping while queued: the queue's dedup keeps one job per library.
        var again = await client.PostAsJsonAsync("/api/v1/scan", new { });
        Assert.Equal(HttpStatusCode.Accepted, again.StatusCode);
        var jobs = queue.GetAllJobs().Where(j => j.LibraryId == movieLibId).ToList();
        Assert.Single(jobs);
    }

    [Fact]
    public async Task Sessions_MustBeAdmin_WhileTokensActForTheirUser()
    {
        // Review HIGH: scanning was admin-only before this endpoint; a plain user
        // SESSION must not gain it (the scope-policy model admits every full
        // session, so the controller enforces the role). write:library TOKENS are
        // the intended credential and work regardless of role.
        var (movieLibId, _) = await SeedLibrariesAsync();
        var user = await Factory.SeedUserAsync("arr-user6");
        var admin = await Factory.SeedUserAsync("arr-admin6", role: UserRole.Admin);

        var userSession = await JwtClient(user).PostAsJsonAsync("/api/v1/scan",
            new { path = @"D:\WebhookTest\Movies\film.mkv" });
        Assert.Equal(HttpStatusCode.Forbidden, userSession.StatusCode);

        var adminSession = await JwtClient(admin).PostAsJsonAsync("/api/v1/scan",
            new { path = @"D:\WebhookTest\Movies\film.mkv" });
        Assert.Equal(HttpStatusCode.Accepted, adminSession.StatusCode);
    }

    [Fact]
    public async Task RealArrPayloads_MapTheNestedPath_AndTestEventIsANoOp()
    {
        // Review MED: Sonarr/Radarr never send a top-level `path` — the webhook
        // schema nests it (series.path / movie.folderPath / episodeFile.path).
        var (movieLibId, tvLibId) = await SeedLibrariesAsync();
        var user = await Factory.SeedUserAsync("arr-user7");
        var token = await MintTokenAsync(user, ApiTokenScopes.WriteLibrary);
        var client = ApiTokenClient(token);

        var sonarr = await client.PostAsJsonAsync("/api/v1/scan", new
        {
            eventType = "Download",
            series = new { path = @"D:\WebhookTest\TV\Some Show" },
            episodeFile = new { path = @"D:\WebhookTest\TV\Some Show\Season 01\e01.mkv" },
        });
        Assert.Equal(HttpStatusCode.Accepted, sonarr.StatusCode);
        Assert.Equal(tvLibId, (await sonarr.Content.ReadFromJsonAsync<ScanDto>())!.libraryId);

        var radarr = await client.PostAsJsonAsync("/api/v1/scan", new
        {
            eventType = "Download",
            movie = new { folderPath = @"D:\WebhookTest\Movies\Some Film (2024)" },
        });
        Assert.Equal(HttpStatusCode.Accepted, radarr.StatusCode);
        Assert.Equal(movieLibId, (await radarr.Content.ReadFromJsonAsync<ScanDto>())!.libraryId);

        // The connection-test button must succeed WITHOUT churning the queue.
        var test = await client.PostAsJsonAsync("/api/v1/scan", new { eventType = "Test" });
        Assert.Equal(HttpStatusCode.OK, test.StatusCode);
    }

    [Fact]
    public async Task AclRestrictedTokenUser_SeesOnlyTheirLibraries_AndHiddenPathsLookNonexistent()
    {
        // Review HIGH: the pathless branch enumerated EVERY library (names + ids)
        // to any caller, bypassing the per-user library allow-list; and the path
        // branch was an oracle for hidden roots.
        var (movieLibId, tvLibId) = await SeedLibrariesAsync();
        var restricted = await Factory.SeedUserAsync("arr-restricted");
        await Factory.WithDbAsync(async db =>
        {
            db.UserLibraryAccess.Add(new UserLibraryAccess { UserId = restricted.Id, LibraryId = movieLibId });
            await db.SaveChangesAsync();
        });
        var token = await MintTokenAsync(restricted, ApiTokenScopes.WriteLibrary);
        var client = ApiTokenClient(token);

        var pathless = await client.PostAsJsonAsync("/api/v1/scan", new { });
        Assert.Equal(HttpStatusCode.Accepted, pathless.StatusCode);
        var jobs = await pathless.Content.ReadFromJsonAsync<List<ScanDto>>();
        Assert.NotEmpty(jobs!);
        Assert.All(jobs!, j => Assert.NotEqual(tvLibId, j.libraryId)); // hidden library never appears

        // A path inside the HIDDEN library answers exactly like an outside path.
        var hidden = await client.PostAsJsonAsync("/api/v1/scan",
            new { path = @"D:\WebhookTest\TV\Some Show" });
        Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode);
    }

    private sealed record MintDto(Guid id, string token);
    private sealed record ScanDto(Guid libraryId, string libraryName, Guid jobId, bool alreadyQueued);
}
