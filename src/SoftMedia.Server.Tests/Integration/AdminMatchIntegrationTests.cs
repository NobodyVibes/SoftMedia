using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SoftMedia.Server.Data;
using SoftMedia.Server.Models;
using SoftMedia.Server.Services.Identity;
using SoftMedia.Server.Tests.Helpers;
using Xunit;

namespace SoftMedia.Server.Tests.Integration;

/// HTTP coverage for the P3-WI-003 admin match endpoints. Auth (admin-only), manual
/// edit auto-locks the item, unlock clears the flag.
public class AdminMatchIntegrationTests : IntegrationTestBase
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

    private async Task<Guid> SeedMovieAsync()
    {
        return await Factory.WithDbAsync(async db =>
        {
            var lib = new Library { Id = Guid.NewGuid(), Name = "AdminMatch-Test", Type = LibraryType.Movie, Paths = new() { "/m" } };
            db.Libraries.Add(lib);
            var item = new MediaItem
            {
                Id = Guid.NewGuid(),
                LibraryId = lib.Id,
                Title = "Wrong Match",
                SortTitle = "Wrong Match",
                Path = "/m/movie.mkv",
                Type = MediaType.Movie,
            };
            db.MediaItems.Add(item);
            await db.SaveChangesAsync();
            return item.Id;
        });
    }

    [Fact]
    public async Task SearchMatch_Anonymous_Returns401()
    {
        var resp = await Factory.CreateClient().PostAsJsonAsync($"/api/v1/admin/match/{Guid.NewGuid()}/search", new { query = "blade runner" });
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task SearchMatch_NonAdmin_Returns403()
    {
        var user = await Factory.SeedUserAsync("matchuser", role: UserRole.User);
        var resp = await ClientFor(user).PostAsJsonAsync($"/api/v1/admin/match/{Guid.NewGuid()}/search", new { query = "x" });
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task ManualEdit_AsAdmin_UpdatesFields_AndAutoLocks()
    {
        var admin = await Factory.SeedUserAsync("matchadmin", role: UserRole.Admin);
        var itemId = await SeedMovieAsync();

        var resp = await ClientFor(admin).PatchAsJsonAsync(
            $"/api/v1/admin/match/{itemId}",
            new { title = "Blade Runner 2049", year = 2017, overview = "Hand-edited" });
        resp.EnsureSuccessStatusCode();

        var (title, year, overview, locked, lockedAt) = await Factory.WithDbAsync(async db =>
        {
            var i = await db.MediaItems.AsNoTracking().FirstAsync(m => m.Id == itemId);
            return (i.Title, i.Year, i.Overview, i.MetadataLocked, i.MetadataLockedAt);
        });
        Assert.Equal("Blade Runner 2049", title);
        Assert.Equal(2017, year);
        Assert.Equal("Hand-edited", overview);
        Assert.True(locked);
        Assert.NotNull(lockedAt);
    }

    [Fact]
    public async Task Unlock_ClearsLock_AndTimestamp()
    {
        var admin = await Factory.SeedUserAsync("matchadmin2", role: UserRole.Admin);
        var itemId = await SeedMovieAsync();
        // Lock first via manual edit.
        await ClientFor(admin).PatchAsJsonAsync($"/api/v1/admin/match/{itemId}", new { title = "X" });

        var resp = await ClientFor(admin).PostAsync($"/api/v1/admin/match/{itemId}/unlock", content: null);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var (locked, lockedAt) = await Factory.WithDbAsync(async db =>
        {
            var i = await db.MediaItems.AsNoTracking().FirstAsync(m => m.Id == itemId);
            return (i.MetadataLocked, i.MetadataLockedAt);
        });
        Assert.False(locked);
        Assert.Null(lockedAt);
    }

    [Fact]
    public async Task SearchMatch_UnknownItem_Returns404()
    {
        var admin = await Factory.SeedUserAsync("matchadmin3", role: UserRole.Admin);
        var resp = await ClientFor(admin).PostAsJsonAsync($"/api/v1/admin/match/{Guid.NewGuid()}/search", new { query = "anything" });
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }
}
