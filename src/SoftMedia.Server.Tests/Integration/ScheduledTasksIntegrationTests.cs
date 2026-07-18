using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using SoftMedia.Server.Models;
using SoftMedia.Server.Services.Identity;
using SoftMedia.Server.Services.Infrastructure;
using SoftMedia.Server.Tests.Helpers;
using Xunit;

namespace SoftMedia.Server.Tests.Integration;

/// Coverage for the scheduled-tasks admin endpoints (P1-WI-005): the registry is
/// seeded with all known tasks, admin auth is enforced, and the manual trigger for
/// metadata refresh reflects in the task's LastRunUtc.
public class ScheduledTasksIntegrationTests : IntegrationTestBase
{
    private record TaskStatusDto(
        string Name, string Description, string Schedule, bool SupportsManualTrigger,
        DateTime? LastRunUtc, long? LastRunDurationMs, string? LastResult, string? LastError, DateTime? NextRunUtc);

    private HttpClient AdminClient(out User admin)
    {
        admin = Factory.SeedUserAsync("taskadmin", role: UserRole.Admin).GetAwaiter().GetResult();
        var client = Factory.CreateClient();
        using var scope = Factory.Services.CreateScope();
        var tokenService = scope.ServiceProvider.GetRequiredService<ITokenService>();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", tokenService.GenerateAccessToken(admin));
        return client;
    }

    [Fact]
    public async Task GetTasks_Anonymous_Returns401()
    {
        var resp = await Factory.CreateClient().GetAsync("/api/v1/admin/tasks");
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task GetTasks_AsAdmin_ListsSeededTasks()
    {
        var client = AdminClient(out _);
        var tasks = await client.GetFromJsonAsync<List<TaskStatusDto>>("/api/v1/admin/tasks");

        Assert.NotNull(tasks);
        // The seeder registers 11 known background tasks.
        Assert.True(tasks!.Count >= 6, $"Expected >= 6 tasks, got {tasks.Count}");
        Assert.Contains(tasks, t => t.Name == ScheduledTaskNames.MetadataRefresh && t.SupportsManualTrigger);
        Assert.Contains(tasks, t => t.Name == ScheduledTaskNames.LibraryWatcher && !t.SupportsManualTrigger);
    }

    [Fact]
    public async Task TriggerMetadataRefresh_UpdatesLastRun()
    {
        var client = AdminClient(out _);

        var resp = await client.PostAsync($"/api/v1/admin/tasks/{Uri.EscapeDataString(ScheduledTaskNames.MetadataRefresh)}/trigger", null);
        Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);

        var tasks = await client.GetFromJsonAsync<List<TaskStatusDto>>("/api/v1/admin/tasks");
        var refresh = tasks!.Single(t => t.Name == ScheduledTaskNames.MetadataRefresh);
        Assert.NotNull(refresh.LastRunUtc);
        Assert.Equal("Success", refresh.LastResult);
    }

    [Fact]
    public async Task TriggerUnsupportedTask_Returns400()
    {
        var client = AdminClient(out _);
        var resp = await client.PostAsync($"/api/v1/admin/tasks/{Uri.EscapeDataString(ScheduledTaskNames.LibraryWatcher)}/trigger", null);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    // --- R-WI-008: scheduled library scans ---

    [Fact]
    public async Task ScheduledLibraryScan_IsRegistered_WithManualTrigger_AndIntervalSeeded()
    {
        var client = AdminClient(out _);

        var tasks = await client.GetFromJsonAsync<List<TaskStatusDto>>("/api/v1/admin/tasks");
        var scan = tasks!.SingleOrDefault(t => t.Name == ScheduledTaskNames.ScheduledLibraryScan);
        Assert.NotNull(scan);
        Assert.True(scan!.SupportsManualTrigger);
        Assert.Equal("Scheduled", scan.Schedule);

        // The interval setting must be seeded (off by default) so the UI can edit it.
        var settings = await client.GetFromJsonAsync<List<AppSetting>>("/api/v1/settings");
        var interval = settings!.SingleOrDefault(s => s.Key == "LibraryScanIntervalHours");
        Assert.NotNull(interval);
        Assert.Equal("0", interval!.Value);
    }

    [Fact]
    public async Task TriggerScheduledLibraryScan_EnqueuesJobForEveryLibrary_AndReportsSuccess()
    {
        var client = AdminClient(out _);

        // Seed two libraries directly (the test host has none by default).
        Guid moviesId, tvId;
        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SoftMedia.Server.Data.AppDbContext>();
            var movies = new Library { Name = "IT Movies", Type = LibraryType.Movie };
            var tv = new Library { Name = "IT Shows", Type = LibraryType.TV };
            db.Libraries.AddRange(movies, tv);
            await db.SaveChangesAsync();
            (moviesId, tvId) = (movies.Id, tv.Id);
        }

        var resp = await client.PostAsync($"/api/v1/admin/tasks/{Uri.EscapeDataString(ScheduledTaskNames.ScheduledLibraryScan)}/trigger", null);
        Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);

        // The generalised trigger dispatched to ScheduledScanService, which enqueued one
        // LibraryScan job per library (jobs stay visible even after fast completion/failure).
        var queue = Factory.Services.GetRequiredService<SoftMedia.Server.Services.Scanning.ILibraryScanQueueService>();
        var jobs = queue.GetAllJobs().Where(j => j.Type == LibraryScanJobType.LibraryScan).ToList();
        Assert.Contains(jobs, j => j.LibraryId == moviesId);
        Assert.Contains(jobs, j => j.LibraryId == tvId);

        var tasks = await client.GetFromJsonAsync<List<TaskStatusDto>>("/api/v1/admin/tasks");
        var scan = tasks!.Single(t => t.Name == ScheduledTaskNames.ScheduledLibraryScan);
        Assert.Equal("Success", scan.LastResult);
        Assert.NotNull(scan.LastRunUtc);
    }
}
