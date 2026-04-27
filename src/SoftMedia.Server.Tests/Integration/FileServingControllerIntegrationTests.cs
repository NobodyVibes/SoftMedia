using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using SoftMedia.Server.Models;
using SoftMedia.Server.Tests.Helpers;
using Xunit;

namespace SoftMedia.Server.Tests.Integration;

/// Todo 09 integration tests — verify every file-serving controller honours
/// its [Authorize] attribute, rejects out-of-library paths, and handles the
/// path-traversal classes the original audit flagged.
public class FileServingControllerIntegrationTests : IntegrationTestBase
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    private async Task<string> GetAccessTokenAsync(string username = "alice", string password = "TestPass!1")
    {
        await Factory.SeedUserAsync(username, password);
        var client = Factory.CreateClient();
        var resp = await client.PostAsJsonAsync("/api/v1/auth/login", new { Username = username, Password = password });
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>(JsonOpts);
        return body.GetProperty("accessToken").GetString()!;
    }

    private HttpClient AuthenticatedClient(string token)
    {
        var client = Factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    // --- Unauthenticated access -------------------------------------------

    [Theory]
    [InlineData("/api/v1/stream/00000000-0000-0000-0000-000000000000")]
    [InlineData("/api/v1/audio/stream/00000000-0000-0000-0000-000000000000")]
    [InlineData("/api/v1/audio/00000000-0000-0000-0000-000000000000/cover")]
    [InlineData("/api/v1/image/proxy?url=https://static.tvmaze.com/anything.jpg")]
    [InlineData("/api/v1/music/album/00000000-0000-0000-0000-000000000000/cover")]
    public async Task ProtectedEndpoint_WithoutToken_Returns401(string url)
    {
        var client = Factory.CreateClient();
        var response = await client.GetAsync(url);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // --- StreamController -------------------------------------------------

    [Fact]
    public async Task Stream_AuthenticatedButUnknownId_Returns404()
    {
        var token = await GetAccessTokenAsync();
        var client = AuthenticatedClient(token);
        var response = await client.GetAsync("/api/v1/stream/" + Guid.NewGuid());
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Stream_PathOutsideLibrary_Returns404()
    {
        var token = await GetAccessTokenAsync();

        // Seed a library at a temp dir and a MediaItem whose Path is OUTSIDE that library.
        var libDir = Path.Combine(Path.GetTempPath(), "softmedia-lib-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(libDir);
        var escapeFile = Path.Combine(Path.GetTempPath(), "escape-" + Guid.NewGuid().ToString("N") + ".mkv");
        await File.WriteAllTextAsync(escapeFile, "escape");

        try
        {
            Guid mediaId = Guid.Empty;
            await Factory.WithDbAsync(async db =>
            {
                var lib = new Library { Id = Guid.NewGuid(), Name = "L", Type = LibraryType.Movie, Paths = new List<string> { libDir } };
                var item = new MediaItem
                {
                    Id = Guid.NewGuid(), LibraryId = lib.Id, Title = "x", SortTitle = "x",
                    Path = escapeFile, Container = "mkv"
                };
                db.Libraries.Add(lib);
                db.MediaItems.Add(item);
                await db.SaveChangesAsync();
                mediaId = item.Id;
            });

            var client = AuthenticatedClient(token);
            var response = await client.GetAsync("/api/v1/stream/" + mediaId);

            // MediaService maps ValidateMediaAccess → null (out-of-library) →
            // controller returns NotFound. A 403 here would indicate the
            // contract changed (e.g. controller started throwing
            // UnauthorizedAccessException) — assert the tight invariant so
            // such a refactor does not slip through silently.
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }
        finally
        {
            try { File.Delete(escapeFile); } catch { }
            try { Directory.Delete(libDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task Stream_ValidItem_ReturnsContentWithRangeSupport()
    {
        var token = await GetAccessTokenAsync();

        var libDir = Path.Combine(Path.GetTempPath(), "softmedia-lib-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(libDir);
        var mediaFile = Path.Combine(libDir, "x.mkv");
        await File.WriteAllBytesAsync(mediaFile, Enumerable.Range(0, 1024).Select(i => (byte)(i & 0xff)).ToArray());

        try
        {
            Guid mediaId = Guid.Empty;
            await Factory.WithDbAsync(async db =>
            {
                var lib = new Library { Id = Guid.NewGuid(), Name = "L", Type = LibraryType.Movie, Paths = new List<string> { libDir } };
                var item = new MediaItem
                {
                    Id = Guid.NewGuid(), LibraryId = lib.Id, Title = "x", SortTitle = "x",
                    Path = mediaFile, Container = "mkv", Size = 1024,
                    Type = MediaType.Movie, ContentRating = "G",
                    // ContentRating set explicitly so the parental-control filter
                    // (default user ceiling MaxRating="PG-13") does not fail-safe
                    // hide a null-rated item. The test is about file serving,
                    // not parental controls.
                };
                db.Libraries.Add(lib);
                db.MediaItems.Add(item);
                await db.SaveChangesAsync();
                mediaId = item.Id;
            });

            var client = AuthenticatedClient(token);
            var full = await client.GetAsync("/api/v1/stream/" + mediaId);
            Assert.Equal(HttpStatusCode.OK, full.StatusCode);
            Assert.Equal(1024, full.Content.Headers.ContentLength);

            var rangeReq = new HttpRequestMessage(HttpMethod.Get, "/api/v1/stream/" + mediaId);
            rangeReq.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 15);
            var range = await client.SendAsync(rangeReq);
            Assert.Equal(HttpStatusCode.PartialContent, range.StatusCode);
        }
        finally
        {
            try { Directory.Delete(libDir, recursive: true); } catch { }
        }
    }

    // --- ImageController --------------------------------------------------

    [Fact]
    public async Task ImageProxy_DisallowedHost_ReturnsBadRequest()
    {
        var token = await GetAccessTokenAsync();
        var client = AuthenticatedClient(token);
        var response = await client.GetAsync("/api/v1/image/proxy?url=https://attacker.example.com/steal.jpg");
        // Tight assertion: a 401/403 here would mean the Authorization gate
        // is tripping before the SSRF allowlist check. That would hide an
        // auth regression behind what looks like an SSRF-deny response.
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ImageProxy_MissingUrl_ReturnsBadRequest()
    {
        var token = await GetAccessTokenAsync();
        var client = AuthenticatedClient(token);
        var response = await client.GetAsync("/api/v1/image/proxy");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // --- Query-string token auth (browser <img> compatibility) ------------

    [Fact]
    public async Task ImageProxy_WithQueryStringToken_PassesAuthorization()
    {
        var token = await GetAccessTokenAsync();
        var client = Factory.CreateClient();
        var response = await client.GetAsync(
            $"/api/v1/image/proxy?access_token={Uri.EscapeDataString(token)}&url=https://attacker.example.com/x.jpg");

        // Query-token auth must get past the 401 gate. The response here will
        // be a 4xx from the disallowed-host check (not from authorization).
        Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task MusicCover_WithQueryStringToken_PassesAuthorization()
    {
        var token = await GetAccessTokenAsync();
        var client = Factory.CreateClient();
        var response = await client.GetAsync(
            $"/api/v1/music/album/{Guid.NewGuid()}/cover?access_token={Uri.EscapeDataString(token)}");

        // Auth passes; album does not exist so we expect a 404 (not 401).
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
