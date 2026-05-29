using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using SoftMedia.Server.Models;
using SoftMedia.Server.Services.Identity;
using SoftMedia.Server.Tests.Helpers;
using Xunit;

namespace SoftMedia.Server.Tests.Integration;

/// HTTP-level coverage for the backup/restore admin endpoints (P1-WI-001) and the
/// anonymous health endpoint. Authorization is the primary contract: backup
/// endpoints are admin-only; health is open.
public class AdminBackupIntegrationTests : IntegrationTestBase
{
    [Fact]
    public async Task Health_IsAnonymous_ReturnsOk()
    {
        var client = Factory.CreateClient();
        var resp = await client.GetAsync("/api/v1/health");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task CreateBackup_Anonymous_Returns401()
    {
        var client = Factory.CreateClient();
        var resp = await client.PostAsync("/api/v1/admin/backup", null);
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task CreateBackup_NonAdmin_Returns403()
    {
        var user = await Factory.SeedUserAsync("plainuser", role: UserRole.User);
        var client = Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", IssueToken(user));

        var resp = await client.PostAsync("/api/v1/admin/backup", null);
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task CreateThenListBackup_AsAdmin_Succeeds()
    {
        var admin = await Factory.SeedUserAsync("backupadmin", role: UserRole.Admin);
        var client = Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", IssueToken(admin));

        var create = await client.PostAsync("/api/v1/admin/backup", null);
        Assert.Equal(HttpStatusCode.OK, create.StatusCode);

        var list = await client.GetFromJsonAsync<List<BackupInfoDto>>("/api/v1/admin/backup");
        Assert.NotNull(list);
        Assert.NotEmpty(list!);
    }

    private string IssueToken(User user)
    {
        using var scope = Factory.Services.CreateScope();
        var tokenService = scope.ServiceProvider.GetRequiredService<ITokenService>();
        return tokenService.GenerateAccessToken(user);
    }

    private record BackupInfoDto(string Id, DateTime CreatedAtUtc, long SizeBytes, bool IsPinned);
}
