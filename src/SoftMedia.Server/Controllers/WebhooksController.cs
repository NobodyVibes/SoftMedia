using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SoftMedia.Server.Data;
using SoftMedia.Server.Extensions;
using SoftMedia.Server.Models;
using SoftMedia.Server.Services.Infrastructure;

namespace SoftMedia.Server.Controllers;

/// <summary>
/// CRUD for the caller's outbound webhook subscriptions (P2-WI-004), plus a "test"
/// action that enqueues a synthetic event so the user can verify their endpoint.
/// </summary>
[ApiController]
[Route("api/v1/[controller]")]
[Authorize]
public class WebhooksController : ControllerBase
{
    private readonly AppDbContext _context;
    private readonly IWebhookDispatcher _dispatcher;

    public WebhooksController(AppDbContext context, IWebhookDispatcher dispatcher)
    {
        _context = context;
        _dispatcher = dispatcher;
    }

    [HttpGet]
    public async Task<IActionResult> List()
    {
        var userId = User.GetUserId();
        var subs = await _context.WebhookSubscriptions
            .Where(w => w.UserId == userId)
            .OrderByDescending(w => w.CreatedAt)
            .ToListAsync();

        // Never return the secret after creation.
        return Ok(subs.Select(w => new WebhookDto(
            w.Id, w.Url, JsonSerializer.Deserialize<List<string>>(w.Events) ?? new(),
            w.Active, w.CreatedAt, w.LastDeliveryAt, w.LastDeliveryStatus)));
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateWebhookRequest request)
    {
        var userId = User.GetUserId();

        if (!Uri.TryCreate(request.Url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            return BadRequest("A valid http(s) URL is required.");

        var events = (request.Events ?? new()).Distinct().ToList();
        if (events.Count == 0) return BadRequest("At least one event is required.");
        foreach (var e in events)
            if (!WebhookEvents.IsValid(e)) return BadRequest($"Unknown event '{e}'.");

        var secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
        var sub = new WebhookSubscription
        {
            UserId = userId,
            Url = request.Url,
            Events = JsonSerializer.Serialize(events),
            Secret = secret,
            Active = true,
        };
        _context.WebhookSubscriptions.Add(sub);
        await _context.SaveChangesAsync();

        // Secret is returned ONCE on creation so the user can configure their receiver.
        return Ok(new CreateWebhookResponse(sub.Id, sub.Url, events, secret));
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id)
    {
        var userId = User.GetUserId();
        var sub = await _context.WebhookSubscriptions.FirstOrDefaultAsync(w => w.Id == id && w.UserId == userId);
        if (sub == null) return NotFound();
        _context.WebhookSubscriptions.Remove(sub);
        await _context.SaveChangesAsync();
        return Ok();
    }

    [HttpPost("{id:guid}/test")]
    public async Task<IActionResult> Test(Guid id)
    {
        var userId = User.GetUserId();
        var sub = await _context.WebhookSubscriptions.FirstOrDefaultAsync(w => w.Id == id && w.UserId == userId);
        if (sub == null) return NotFound();

        // Enqueue a synthetic event targeted at all of the user's subscriptions that
        // listen for webhook.test. Ensure this sub listens, regardless of its config.
        var events = JsonSerializer.Deserialize<List<string>>(sub.Events) ?? new();
        if (!events.Contains(WebhookEvents.Test))
        {
            events.Add(WebhookEvents.Test);
            sub.Events = JsonSerializer.Serialize(events);
            await _context.SaveChangesAsync();
        }

        _dispatcher.Enqueue(new WebhookEvent(
            WebhookEvents.Test,
            new { message = "This is a test event from SoftMedia.", subscriptionId = sub.Id },
            userId, User.Identity?.Name));
        return Accepted(new { message = "Test event enqueued." });
    }
}

public record WebhookDto(Guid Id, string Url, List<string> Events, bool Active, DateTime CreatedAt, DateTime? LastDeliveryAt, string? LastDeliveryStatus);
public record CreateWebhookRequest(string Url, List<string>? Events);
public record CreateWebhookResponse(Guid Id, string Url, List<string> Events, string Secret);
