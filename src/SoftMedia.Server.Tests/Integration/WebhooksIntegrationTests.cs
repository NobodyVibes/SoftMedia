using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using SoftMedia.Server.Models;
using SoftMedia.Server.Services.Identity;
using SoftMedia.Server.Tests.Helpers;
using Xunit;

namespace SoftMedia.Server.Tests.Integration;

/// HTTP coverage for webhook subscription CRUD (P2-WI-004): auth, create returns the
/// secret once, list omits it, validation rejects bad URLs/events, delete works.
public class WebhooksIntegrationTests : IntegrationTestBase
{
    private record CreateResp(Guid id, string url, List<string> events, string secret);
    private record ListItem(Guid id, string url, List<string> events, bool active, DateTime createdAt, DateTime? lastDeliveryAt, string? lastDeliveryStatus);

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
    public async Task List_Anonymous_Returns401()
    {
        var resp = await Factory.CreateClient().GetAsync("/api/v1/webhooks");
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Create_ReturnsSecretOnce_ListOmitsIt()
    {
        var user = await Factory.SeedUserAsync("wh1");
        var client = ClientFor(user);

        var create = await client.PostAsJsonAsync("/api/v1/webhooks",
            new { url = "https://example.com/hook", events = new[] { "library.scan.completed" } });
        create.EnsureSuccessStatusCode();
        var created = await create.Content.ReadFromJsonAsync<CreateResp>();
        Assert.False(string.IsNullOrEmpty(created!.secret));

        var list = await client.GetFromJsonAsync<List<ListItem>>("/api/v1/webhooks");
        Assert.Single(list!);
        // The list DTO has no secret field at all — verify the raw JSON doesn't leak it.
        var raw = await (await client.GetAsync("/api/v1/webhooks")).Content.ReadAsStringAsync();
        Assert.DoesNotContain(created.secret, raw);
    }

    [Fact]
    public async Task Create_RejectsInvalidUrl()
    {
        var user = await Factory.SeedUserAsync("wh2");
        var resp = await ClientFor(user).PostAsJsonAsync("/api/v1/webhooks",
            new { url = "not-a-url", events = new[] { "library.scan.completed" } });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Create_RejectsUnknownEvent()
    {
        var user = await Factory.SeedUserAsync("wh3");
        var resp = await ClientFor(user).PostAsJsonAsync("/api/v1/webhooks",
            new { url = "https://example.com/hook", events = new[] { "media.added" } }); // deferred event, not valid in v1
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Delete_RemovesSubscription()
    {
        var user = await Factory.SeedUserAsync("wh4");
        var client = ClientFor(user);
        var created = await (await client.PostAsJsonAsync("/api/v1/webhooks",
            new { url = "https://example.com/hook", events = new[] { "library.scan.failed" } }))
            .Content.ReadFromJsonAsync<CreateResp>();

        var del = await client.DeleteAsync($"/api/v1/webhooks/{created!.id}");
        Assert.Equal(HttpStatusCode.OK, del.StatusCode);

        var list = await client.GetFromJsonAsync<List<ListItem>>("/api/v1/webhooks");
        Assert.Empty(list!);
    }

    [Fact]
    public async Task Test_EnqueuesEvent_Returns202()
    {
        var user = await Factory.SeedUserAsync("wh5");
        var client = ClientFor(user);
        var created = await (await client.PostAsJsonAsync("/api/v1/webhooks",
            new { url = "https://example.com/hook", events = new[] { "library.scan.completed" } }))
            .Content.ReadFromJsonAsync<CreateResp>();

        var test = await client.PostAsync($"/api/v1/webhooks/{created!.id}/test", null);
        Assert.Equal(HttpStatusCode.Accepted, test.StatusCode);
    }

    [Fact]
    public async Task Cannot_Delete_AnotherUsersWebhook()
    {
        var owner = await Factory.SeedUserAsync("wh6owner");
        var other = await Factory.SeedUserAsync("wh6other");
        var created = await (await ClientFor(owner).PostAsJsonAsync("/api/v1/webhooks",
            new { url = "https://example.com/hook", events = new[] { "webhook.test" } }))
            .Content.ReadFromJsonAsync<CreateResp>();

        var del = await ClientFor(other).DeleteAsync($"/api/v1/webhooks/{created!.id}");
        Assert.Equal(HttpStatusCode.NotFound, del.StatusCode);
    }
}
