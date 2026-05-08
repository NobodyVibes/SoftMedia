using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using SoftMedia.Server.Models;
using SoftMedia.Server.Tests.Helpers;
using Xunit;

namespace SoftMedia.Server.Tests.Integration;

/// Diagnostic — full HTTP round-trip from /api/v1/libraries/{id}/items
/// to the JSON the React client receives. Confirms that posterPath is
/// included in the response payload for movies that have PosterUrl set.
public class LibraryItemsPosterIntegrationTests : IntegrationTestBase
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    private async Task<string> GetAccessTokenAsync()
    {
        // Seed as Admin so the parental-control filter doesn't strip movies
        // with null ContentRating (matches the typical production user state).
        await Factory.SeedUserAsync("alice", "TestPass!1", role: UserRole.Admin);
        var client = Factory.CreateClient();
        var resp = await client.PostAsJsonAsync("/api/v1/auth/login",
            new { Username = "alice", Password = "TestPass!1" });
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

    [Fact]
    public async Task GetLibraryItems_ReturnsPosterPathForMovieWithPosterUrl()
    {
        var token = await GetAccessTokenAsync();

        // Seed a Movie library with a movie that has a real OMDb-style poster URL.
        var libraryDir = Path.Combine(Path.GetTempPath(), "softmedia-libposter-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(libraryDir);
        var moviePath = Path.Combine(libraryDir, "inception.mkv");
        await File.WriteAllTextAsync(moviePath, "stub");

        Guid libraryId = Guid.Empty;
        await Factory.WithDbAsync(async db =>
        {
            var lib = new Library
            {
                Id = Guid.NewGuid(),
                Name = "Movies",
                Type = LibraryType.Movie,
                Paths = new List<string> { libraryDir }
            };
            var movie = new MediaItem
            {
                Id = Guid.NewGuid(),
                LibraryId = lib.Id,
                Title = "Inception",
                SortTitle = "Inception",
                Path = moviePath,
                Type = MediaType.Movie,
                PosterUrl = "https://m.media-amazon.com/images/M/MV5BMjAxMzY3NjcxNF5BMl5BanBnXkFtZTcwNTI5OTM0Mw@@._V1_SX300.jpg",
            };
            db.Libraries.Add(lib);
            db.MediaItems.Add(movie);
            await db.SaveChangesAsync();
            libraryId = lib.Id;
        });

        var client = AuthenticatedClient(token);
        var response = await client.GetAsync($"/api/v1/libraries/{libraryId}/items");
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(body);

        // Walk into PagedResult<MediaItemDto>
        var items = json.RootElement.GetProperty("items");
        Assert.True(items.GetArrayLength() >= 1, $"Expected at least one item in response. Body: {body}");

        var first = items[0];

        // Surface the actual JSON property names so a future failure tells us
        // exactly what the client is missing.
        var fieldNames = string.Join(", ", first.EnumerateObject().Select(p => p.Name));

        Assert.True(first.TryGetProperty("posterPath", out var posterPath),
            $"Response does NOT contain 'posterPath'. Properties present: {fieldNames}");

        var posterPathStr = posterPath.GetString();
        Assert.False(string.IsNullOrEmpty(posterPathStr),
            $"posterPath was present but empty/null. Full JSON: {body}");

        Assert.StartsWith("/api/v1/image/proxy?url=", posterPathStr);

        try { Directory.Delete(libraryDir, recursive: true); } catch { }
    }
}
