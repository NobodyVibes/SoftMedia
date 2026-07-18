using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using SoftMedia.Server.Data;
using SoftMedia.Server.Models;
using SoftMedia.Server.Services.Infrastructure;
using SoftMedia.Server.Services.Security.ContentRating;
using Xunit;

namespace SoftMedia.Server.Tests.Services.Infrastructure;

/// R-WI-010 — the DLNA settings group must be fully seeded (DlnaMaxContentRatings was previously
/// read with a default but never seeded, so it was invisible/uneditable), and the per-type rating
/// JSON the admin card produces must parse into a real ceiling (guarding the fail-open where a
/// malformed/absent value silently disables the DLNA parental cap).
public class SettingsServiceDlnaTests
{
    private static SettingsService NewService(out AppDbContext ctx)
    {
        ctx = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"settings-dlna-{Guid.NewGuid()}").Options);
        return new SettingsService(ctx, NullLogger<SettingsService>.Instance, new MemoryCache(new MemoryCacheOptions()));
    }

    [Fact]
    public async Task InitializeDefaults_SeedsAllDlnaKeys_IncludingMaxContentRatings()
    {
        var svc = NewService(out var ctx);
        await svc.InitializeDefaultsAsync();

        foreach (var key in new[] { "EnableDlna", "DlnaServerName", "DlnaExposedLibraries", "DlnaMaxContentRatings" })
            Assert.True(await ctx.Settings.AnyAsync(s => s.Key == key), $"{key} not seeded");

        var ratings = await ctx.Settings.FirstAsync(s => s.Key == "DlnaMaxContentRatings");
        Assert.Equal("", ratings.Value); // default = no cap, preserves prior behaviour
        Assert.Equal("DLNA", ratings.Group);
    }

    [Fact]
    public void DlnaRatingsJson_FromCard_ParsesIntoEnforcedCeiling()
    {
        // The exact per-type shape DlnaSettingsCard serializes must yield an ENFORCED ceiling, not an
        // empty (fail-open) one — DlnaContentDirectory builds the ceiling via UserRatingCeilings.From.
        var ceilings = UserRatingCeilings.From(new User
        {
            MaxRating = "",
            ContentRatings = "{\"Movie\":\"PG-13\",\"TV\":\"TV-PG\"}",
        });

        Assert.False(ceilings.IsUnrestricted);
        Assert.Equal("PG-13", ceilings.Movie);
        Assert.Equal("TV-PG", ceilings.Tv);
        Assert.Null(ceilings.Game); // no Game entry → unrestricted for games only
    }
}
