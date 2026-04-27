using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using SoftMedia.Server.Data;
using SoftMedia.Server.Models;
using SoftMedia.Server.Services.Identity;
using SoftMedia.Server.Tests.Helpers;
using Xunit;

namespace SoftMedia.Server.Tests.Integration;

/// Phase 3 / B6 — end-to-end test of the parental-control gate.
///
/// Drives a real HTTP request through `WebApplicationFactory<Program>` so the
/// full pipeline (JwtBearer → controller → MediaService → repo with rating
/// filter) is exercised. The test seeds a Movie library with G/PG-13/R items
/// and a Series with TV-MA, then asserts each user role receives the right
/// subset.
public class ParentalControlIntegrationTests : IntegrationTestBase
{
    [Fact]
    public async Task ChildAccount_CannotStreamRRatedMovie()
    {
        var (rRatedId, _) = await SeedItemsAsync();
        var child = await Factory.SeedUserAsync("child", role: UserRole.User);
        await SetCeilingsAsync(child.Id, """{"Movie":"PG-13"}""");

        var token = IssueToken(child);
        var client = Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await client.GetAsync($"/api/v1/Stream/{rRatedId}");

        // 404 on disallowed item (anti-probe behaviour: indistinguishable from
        // "item does not exist" so a child account cannot enumerate IDs).
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task AdminAccount_CanStreamRRatedMovie()
    {
        // Seed a real file on disk for the R-rated item so we can assert OK
        // rather than the ambiguous "OK or NotFound" — admin bypass is the
        // contract under test, not file-presence handling.
        var libDir = Path.Combine(Path.GetTempPath(), "softmedia-pc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(libDir);
        var mediaFile = Path.Combine(libDir, "r-movie.mkv");
        await File.WriteAllBytesAsync(mediaFile, new byte[] { 0x1A, 0x45, 0xDF, 0xA3 });

        try
        {
            var rRatedId = await Factory.WithDbAsync(async db =>
            {
                var lib = new Library
                {
                    Id = Guid.NewGuid(),
                    Name = "Admin-Test",
                    Type = LibraryType.Movie,
                    Paths = new List<string> { libDir },
                };
                db.Libraries.Add(lib);
                var item = new MediaItem
                {
                    Id = Guid.NewGuid(),
                    LibraryId = lib.Id,
                    Title = "R-Movie-AdminTest",
                    SortTitle = "R-Movie-AdminTest",
                    Path = mediaFile,
                    Type = MediaType.Movie,
                    ContentRating = "R",
                };
                db.MediaItems.Add(item);
                await db.SaveChangesAsync();
                return item.Id;
            });

            var admin = await Factory.SeedUserAsync("admin1", role: UserRole.Admin);
            var token = IssueToken(admin);
            var client = Factory.CreateClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

            var response = await client.GetAsync($"/api/v1/Stream/{rRatedId}");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        finally
        {
            try { Directory.Delete(libDir, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public async Task ChildAccount_LibraryListingHidesRRatedItems()
    {
        var (_, libraryId) = await SeedItemsAsync();
        var child = await Factory.SeedUserAsync("child2", role: UserRole.User);
        await SetCeilingsAsync(child.Id, """{"Movie":"PG-13"}""");

        var token = IssueToken(child);
        var client = Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await client.GetAsync($"/api/v1/Libraries/{libraryId}/items?page=1&pageSize=50");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadAsStringAsync();
        // R/NC-17/Unrated must not appear in the JSON; G/PG/PG-13 should.
        Assert.Contains("PG13-Movie-Test", json);
        Assert.DoesNotContain("R-Movie-Test", json);
        Assert.DoesNotContain("NC17-Movie-Test", json);
        Assert.DoesNotContain("Unrated-Movie-Test", json);
    }

    [Fact]
    public async Task AdminAccount_LibraryListingIncludesEverything()
    {
        var (_, libraryId) = await SeedItemsAsync();
        var admin = await Factory.SeedUserAsync("admin2", role: UserRole.Admin);

        var token = IssueToken(admin);
        var client = Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await client.GetAsync($"/api/v1/Libraries/{libraryId}/items?page=1&pageSize=50");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadAsStringAsync();
        Assert.Contains("R-Movie-Test", json);
        Assert.Contains("NC17-Movie-Test", json);
        Assert.Contains("Unrated-Movie-Test", json);
    }

    // ---- helpers ----------------------------------------------------------

    /// Returns (idOfRRatedMovie, libraryId).
    private async Task<(Guid rId, Guid libraryId)> SeedItemsAsync()
    {
        return await Factory.WithDbAsync(async db =>
        {
            var lib = new Library
            {
                Id = Guid.NewGuid(),
                Name = "P-Test",
                Type = LibraryType.Movie,
                Paths = new List<string> { Path.GetTempPath() },
            };
            db.Libraries.Add(lib);

            var items = new[]
            {
                Movie(lib, "G-Movie-Test", "G"),
                Movie(lib, "PG13-Movie-Test", "PG-13"),
                Movie(lib, "R-Movie-Test", "R"),
                Movie(lib, "NC17-Movie-Test", "NC-17"),
                Movie(lib, "Unrated-Movie-Test", null),
            };
            db.MediaItems.AddRange(items);
            await db.SaveChangesAsync();

            var rRated = items.First(m => m.Title == "R-Movie-Test");
            return (rRated.Id, lib.Id);
        });
    }

    private async Task SetCeilingsAsync(Guid userId, string contentRatingsJson)
    {
        await Factory.WithDbAsync(async db =>
        {
            var user = await db.Users.FindAsync(userId);
            user!.ContentRatings = contentRatingsJson;
            await db.SaveChangesAsync();
        });
    }

    private string IssueToken(User user)
    {
        using var scope = Factory.Services.CreateScope();
        var tokenService = scope.ServiceProvider.GetRequiredService<ITokenService>();
        return tokenService.GenerateAccessToken(user);
    }

    private static MediaItem Movie(Library lib, string title, string? rating) => new()
    {
        Id = Guid.NewGuid(),
        LibraryId = lib.Id,
        Title = title,
        SortTitle = title,
        Path = Path.Combine(Path.GetTempPath(), $"{title}.mkv"),
        Type = MediaType.Movie,
        ContentRating = rating,
        DateAdded = DateTime.UtcNow,
    };
}
