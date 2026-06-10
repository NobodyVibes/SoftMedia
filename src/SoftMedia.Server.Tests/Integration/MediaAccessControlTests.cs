using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using SoftMedia.Server.Models;
using SoftMedia.Server.Tests.Helpers;
using Xunit;

namespace SoftMedia.Server.Tests.Integration;

/// Security audit H1/M4/L3: per-id media endpoints that previously bypassed the ACL-aware
/// repository now enforce the per-user library allow-list. A restricted user must NOT be
/// able to read metadata / track info / artwork for an item in a library they're denied,
/// while an admin (unrestricted) still can. Photo-type items are used so the content-rating
/// filter is a no-op (it only gates Movie/TV/Game), isolating the library-ACL behaviour.
public class MediaAccessControlTests : IntegrationTestBase
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };
    private record AuthResponseDto(string AccessToken);

    private Guid _allowedLibId, _deniedLibId, _allowedItemId, _deniedItemId;

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();

        _allowedLibId = Guid.NewGuid();
        _deniedLibId = Guid.NewGuid();
        _allowedItemId = Guid.NewGuid();
        _deniedItemId = Guid.NewGuid();

        // A restricted user with an allow-list that includes only the "allowed" library.
        var user = await Factory.SeedUserAsync("restricted");

        await Factory.WithDbAsync(async db =>
        {
            db.Libraries.Add(new Library { Id = _allowedLibId, Name = "Allowed", Type = LibraryType.Photo, Paths = new() { "/allowed" } });
            db.Libraries.Add(new Library { Id = _deniedLibId, Name = "Denied", Type = LibraryType.Photo, Paths = new() { "/denied" } });
            db.MediaItems.Add(new MediaItem
            {
                Id = _allowedItemId, LibraryId = _allowedLibId, Title = "AllowedItem",
                SortTitle = "AllowedItem", Path = "/allowed/a.jpg", Type = MediaType.Photo,
            });
            db.MediaItems.Add(new MediaItem
            {
                Id = _deniedItemId, LibraryId = _deniedLibId, Title = "DeniedItem",
                SortTitle = "DeniedItem", Path = "/denied/b.jpg", Type = MediaType.Photo,
            });
            // Allow-list row => user sees ONLY the allowed library.
            db.UserLibraryAccess.Add(new UserLibraryAccess { UserId = user.Id, LibraryId = _allowedLibId });
            await db.SaveChangesAsync();
        });
    }

    private async Task<string> LoginAsync(string username, string password = "TestPass!1")
    {
        var client = Factory.CreateClient();
        var login = await client.PostAsJsonAsync("/api/v1/auth/login", new { Username = username, Password = password });
        login.EnsureSuccessStatusCode();
        return (await login.Content.ReadFromJsonAsync<AuthResponseDto>(JsonOpts))!.AccessToken;
    }

    private async Task<HttpResponseMessage> GetAsync(string url, string token)
    {
        var client = Factory.CreateClient();
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await client.SendAsync(req);
    }

    [Fact]
    public async Task RestrictedUser_CannotReadMetadata_OfDeniedLibraryItem_M4()
    {
        var token = await LoginAsync("restricted");

        // Denied -> 404; allowed -> 200. (The metadata endpoint needs no file on disk.)
        Assert.Equal(HttpStatusCode.NotFound, (await GetAsync($"/api/v1/media/{_deniedItemId}", token)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await GetAsync($"/api/v1/media/{_allowedItemId}", token)).StatusCode);
    }

    [Fact]
    public async Task AdminUser_CanReadMetadata_OfAnyLibraryItem_M4Control()
    {
        await Factory.SeedUserAsync("adminuser", role: UserRole.Admin);
        var token = await LoginAsync("adminuser");

        // Proves the denied item exists and is blocked ONLY by the library ACL,
        // not by the content-rating filter or a missing row.
        Assert.Equal(HttpStatusCode.OK, (await GetAsync($"/api/v1/media/{_deniedItemId}", token)).StatusCode);
    }

    [Fact]
    public async Task RestrictedUser_GetsNotFound_OnTrackAndArtworkEndpoints_OfDeniedItem_H1L3()
    {
        var token = await LoginAsync("restricted");

        // MediaTracksController (H1) — denied item is rejected at the ACL stage, 404.
        Assert.Equal(HttpStatusCode.NotFound, (await GetAsync($"/api/media/{_deniedItemId}/duration", token)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await GetAsync($"/api/media/{_deniedItemId}/tracks", token)).StatusCode);

        // Artwork/scrubber endpoints (L3) — all gated by the same ACL.
        Assert.Equal(HttpStatusCode.NotFound, (await GetAsync($"/api/v1/music/album/{_deniedItemId}/cover", token)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await GetAsync($"/api/v1/trickplay/{_deniedItemId}/manifest.json", token)).StatusCode);
    }
}
