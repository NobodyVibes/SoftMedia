using System.Net;
using Microsoft.EntityFrameworkCore;
using SoftMedia.Server.Tests.Helpers;
using Xunit;

namespace SoftMedia.Server.Tests.Integration;

/// Confirms the WebApplicationFactory boots the full pipeline so the heavier
/// integration suites have a known-good starting point.
public class FactorySmokeTests : IntegrationTestBase
{
    [Fact]
    public async Task Factory_BootsAndServesRequests()
    {
        var client = Factory.CreateClient();
        var response = await client.GetAsync("/swagger/v1/swagger.json");
        // Swagger responds only in the Development environment; in Testing it
        // may return 404. Either way, the factory booted without throwing,
        // which is the real assertion here.
        Assert.True(response.StatusCode == HttpStatusCode.OK ||
                    response.StatusCode == HttpStatusCode.NotFound,
            $"Unexpected status {response.StatusCode}");
    }

    [Fact]
    public async Task UnauthenticatedCall_ToProtectedEndpoint_Returns401()
    {
        var client = Factory.CreateClient();
        var response = await client.GetAsync("/api/v1/media/00000000-0000-0000-0000-000000000000");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task SeedNoise_IsCleared()
    {
        await Factory.WithDbAsync(async db =>
        {
            var leftover = await db.Libraries
                .FirstOrDefaultAsync(l => l.Name == "Test Movies");
            Assert.Null(leftover);
        });
    }
}
