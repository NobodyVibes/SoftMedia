using System.Net;
using Microsoft.Extensions.DependencyInjection;
using SoftMedia.Server.Tests.Helpers;
using Xunit;

namespace SoftMedia.Server.Tests.Integration;

/// NR-WI-007 — the OpenAPI contract serves outside Development, gated by the
/// EnableApiDocs runtime setting (default on, toggle needs no restart). The test
/// host runs as environment "Testing", so the production gate is what's exercised.
public class ApiDocsIntegrationTests : IntegrationTestBase
{
    private async Task SetApiDocsEnabledAsync(string value)
    {
        // Through the settings service, not a raw DB write — the service invalidates its
        // per-key memory cache on update, which is exactly what the admin toggle does.
        using var scope = Factory.Services.CreateScope();
        var settings = scope.ServiceProvider
            .GetRequiredService<SoftMedia.Server.Services.Infrastructure.ISettingsService>();
        var row = await settings.GetSettingAsync("EnableApiDocs")
            ?? throw new InvalidOperationException("EnableApiDocs not seeded");
        row.Value = value;
        await settings.UpdateSettingsAsync(new List<SoftMedia.Server.Models.AppSetting> { row });
    }

    [Fact]
    public async Task SwaggerJson_Serves_WhenEnabled_Default()
    {
        var resp = await Factory.CreateClient().GetAsync("/swagger/v1/swagger.json");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("\"openapi\"", body);
        // Spot-check that the contract covers the native-client surface. Paths carry the
        // literal [controller] token casing (e.g. /api/v1/Auth/login) — routing itself is
        // case-insensitive, so compare that way here too.
        Assert.Contains("/api/v1/auth/login", body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("/api/v1/transcode", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SwaggerJson_Is404_WhenDisabled_NoRestartNeeded()
    {
        await SetApiDocsEnabledAsync("false");
        try
        {
            var resp = await Factory.CreateClient().GetAsync("/swagger/v1/swagger.json");
            Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);

            // Flipping back on takes effect immediately on the same host.
            await SetApiDocsEnabledAsync("true");
            var after = await Factory.CreateClient().GetAsync("/swagger/v1/swagger.json");
            Assert.Equal(HttpStatusCode.OK, after.StatusCode);
        }
        finally
        {
            await SetApiDocsEnabledAsync("true");
        }
    }
}
