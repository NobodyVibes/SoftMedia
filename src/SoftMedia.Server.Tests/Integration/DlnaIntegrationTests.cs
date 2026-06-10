using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using SoftMedia.Server.Data;
using SoftMedia.Server.Models;
using SoftMedia.Server.Services.Infrastructure;
using SoftMedia.Server.Tests.Helpers;
using Xunit;

namespace SoftMedia.Server.Tests.Integration;

/// P4-004 — the DLNA HTTP surface. Verifies the opt-in gate (404 when off), unauthenticated
/// access when on (no Authorization header), the device description, and a SOAP Browse round
/// trip. SSDP discovery + real-TV rendering are out of scope for an in-process test.
///
/// Uses a test factory that sets RemoteIpAddress to loopback (same pattern as
/// ForwardedHeadersIntegrationTests) so the production LAN-only gate — correctly fail-safe on a
/// null IP — sees a LAN client.
public class DlnaIntegrationTests : IAsyncLifetime
{
    private DlnaTestFactory _factory = null!;

    public async Task InitializeAsync()
    {
        _factory = new DlnaTestFactory();
        _ = _factory.Services;
        await _factory.ResetSeedNoiseAsync();
    }

    public Task DisposeAsync()
    {
        _factory.Dispose();
        return Task.CompletedTask;
    }

    private async Task EnableDlnaAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var s = await db.Settings.FindAsync("EnableDlna");
        if (s == null) db.Settings.Add(new AppSetting { Key = "EnableDlna", Value = "true", Group = "DLNA", Description = "" });
        else s.Value = "true";
        await db.SaveChangesAsync();
        // Clear the settings cache so the next request sees the new value.
        var settings = scope.ServiceProvider.GetRequiredService<ISettingsService>();
        await settings.UpdateSettingsAsync(new List<AppSetting> { new() { Key = "EnableDlna", Value = "true" } });
    }

    [Fact]
    public async Task Disabled_Returns404_NoUnauthenticatedExposure()
    {
        var resp = await _factory.CreateClient().GetAsync("/dlna/description.xml");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Enabled_ServesDeviceDescription_Unauthenticated()
    {
        await EnableDlnaAsync();

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = null; // explicitly no auth
        var resp = await client.GetAsync("/dlna/description.xml");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var xml = await resp.Content.ReadAsStringAsync();
        Assert.Contains("urn:schemas-upnp-org:device:MediaServer:1", xml);
        Assert.Contains("<UDN>uuid:", xml);
        Assert.Contains("ContentDirectory", xml);
    }

    private async Task SetExposedLibrariesAsync(string csv)
    {
        using var scope = _factory.Services.CreateScope();
        var settings = scope.ServiceProvider.GetRequiredService<ISettingsService>();
        await settings.UpdateSettingsAsync(new List<AppSetting> { new() { Key = "DlnaExposedLibraries", Value = csv } });
    }

    [Fact]
    public async Task Enabled_BrowseRoot_ReturnsOnlyExposedLibrariesInDidl()
    {
        var exposedLib = Guid.NewGuid();
        var hiddenLib = Guid.NewGuid();
        await _factory.WithDbAsync(async db =>
        {
            db.Libraries.Add(new Library { Id = exposedLib, Name = "DLNA Movies", Type = LibraryType.Movie, Paths = new() { "/m" } });
            db.Libraries.Add(new Library { Id = hiddenLib, Name = "Secret Films", Type = LibraryType.Movie, Paths = new() { "/s" } });
            await db.SaveChangesAsync();
        });
        await EnableDlnaAsync();
        // Audit M7: only the explicitly-exposed library is browsable.
        await SetExposedLibrariesAsync(exposedLib.ToString());

        var soap = """
<?xml version="1.0"?>
<s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/"><s:Body>
<u:Browse xmlns:u="urn:schemas-upnp-org:service:ContentDirectory:1">
<ObjectID>0</ObjectID><BrowseFlag>BrowseDirectChildren</BrowseFlag><Filter>*</Filter>
<StartingIndex>0</StartingIndex><RequestedCount>0</RequestedCount><SortCriteria></SortCriteria>
</u:Browse></s:Body></s:Envelope>
""";
        var content = new StringContent(soap, System.Text.Encoding.UTF8, "text/xml");
        content.Headers.Add("SOAPACTION", "\"urn:schemas-upnp-org:service:ContentDirectory:1#Browse\"");

        var resp = await _factory.CreateClient().PostAsync("/dlna/cd/control", content);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var xml = await resp.Content.ReadAsStringAsync();
        Assert.Contains("BrowseResponse", xml);
        Assert.Contains("DLNA Movies", xml);        // the exposed library appears
        Assert.DoesNotContain("Secret Films", xml); // the non-exposed library is hidden
    }

    [Fact]
    public async Task Media_NonExposedItem_Returns404()
    {
        // Audit L9: a media item in a library that is NOT DLNA-exposed must not be servable by id,
        // even when DLNA is enabled and the caller is on the LAN.
        var hiddenLib = Guid.NewGuid();
        var itemId = Guid.NewGuid();
        await _factory.WithDbAsync(async db =>
        {
            db.Libraries.Add(new Library { Id = hiddenLib, Name = "Hidden", Type = LibraryType.Movie, Paths = new() { "/h" } });
            db.MediaItems.Add(new MediaItem { Id = itemId, Title = "Private", SortTitle = "private", Path = "/h/x.mkv", LibraryId = hiddenLib, Type = MediaType.Movie });
            await db.SaveChangesAsync();
        });
        await EnableDlnaAsync();
        await SetExposedLibrariesAsync(""); // nothing exposed

        var resp = await _factory.CreateClient().GetAsync($"/dlna/media/{itemId}");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    /// Test-only factory: sets Connection.RemoteIpAddress = loopback so the LAN-only DLNA gate
    /// treats the in-process test client as a LAN device (mirrors ForwardedHeadersIntegrationTests).
    private class DlnaTestFactory : SoftMediaWebApplicationFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureServices(services => services.AddSingleton<IStartupFilter, LoopbackRemoteIpFilter>());
        }
    }

    private class LoopbackRemoteIpFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (ctx, n) =>
            {
                ctx.Connection.RemoteIpAddress ??= IPAddress.Loopback;
                await n();
            });
            next(app);
        };
    }
}
