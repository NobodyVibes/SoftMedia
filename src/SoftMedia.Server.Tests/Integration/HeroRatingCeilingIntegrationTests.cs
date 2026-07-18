using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SoftMedia.Server.Models;
using SoftMedia.Server.Services.Abstractions;
using SoftMedia.Server.Services.Identity;
using SoftMedia.Server.Tests.Helpers;
using Xunit;

namespace SoftMedia.Server.Tests.Integration;

/// B-19 — the hero rotation's shared cache is built unfiltered; the content-rating
/// ceiling must be applied at READ time alongside the existing library ACL, or a
/// restricted user sees over-ceiling titles (name/poster/overview) in the hero.
public class HeroRatingCeilingIntegrationTests : IntegrationTestBase
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

    [Fact]
    public async Task RestrictedUser_DoesNotSeeOverCeilingTitles_InTheHero()
    {
        await Factory.WithDbAsync(async db =>
        {
            var lib = new Library { Id = Guid.NewGuid(), Name = $"Hero-{Guid.NewGuid():N}"[..10], Type = LibraryType.Movie, Paths = new() { "/h" } };
            db.Libraries.Add(lib);
            MediaItem Movie(string title, string rating) => new()
            {
                Id = Guid.NewGuid(),
                LibraryId = lib.Id,
                Title = title,
                SortTitle = title,
                Path = $"/h/{title}.mkv",
                Type = MediaType.Movie,
                ContentRating = rating,
                Overview = "An overview long enough to be hero-worthy.",
                PosterUrl = "/cache/images/x.jpg",
                BackdropUrl = "/cache/images/y.jpg",
                CommunityRating = 8,
            };
            db.MediaItems.Add(Movie("Hero Family Movie", "G"));
            db.MediaItems.Add(Movie("Hero Adult Movie", "R"));
            await db.SaveChangesAsync();
        });

        // Build the shared cache (unfiltered by design).
        using (var scope = Factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<IRecommendationService>().UpdateHeroCacheAsync();
        }

        var kid = await Factory.SeedUserAsync("hero-kid");
        await Factory.WithDbAsync(async db =>
        {
            (await db.Users.FindAsync(kid.Id))!.MaxRating = "G";
            await db.SaveChangesAsync();
        });
        kid.MaxRating = "G";
        var adult = await Factory.SeedUserAsync("hero-adult");

        async Task<List<string>> HeroTitles(User u)
        {
            var json = await ClientFor(u).GetStringAsync("/api/v1/media/hero");
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.EnumerateArray().Select(i => i.GetProperty("title").GetString()!).ToList();
        }

        var kidTitles = await HeroTitles(kid);
        Assert.DoesNotContain("Hero Adult Movie", kidTitles);

        var adultTitles = await HeroTitles(adult);
        Assert.Contains("Hero Adult Movie", adultTitles);
    }
}
