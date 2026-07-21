using System.Security.Claims;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SoftMedia.Server.Data;
using SoftMedia.Server.Hubs;
using SoftMedia.Server.Models;
using Xunit;

namespace SoftMedia.Server.Tests.Hubs;

/// L-18 — scan-progress broadcasts go to the "scan-admins" group, populated at
/// connect time from the DB (the media token the SPA connects with deliberately
/// carries no role claim, so claims-based role checks would never match).
public class MediaHubAdminGroupTests
{
    private readonly AppDbContext _db;
    private readonly IServiceScopeFactory _scopeFactory;

    public MediaHubAdminGroupTests()
    {
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"mediahub-admin-{Guid.NewGuid()}")
            .Options);

        var provider = new Mock<IServiceProvider>();
        provider.Setup(p => p.GetService(typeof(AppDbContext))).Returns(_db);
        var scope = new Mock<IServiceScope>();
        scope.Setup(s => s.ServiceProvider).Returns(provider.Object);
        var factory = new Mock<IServiceScopeFactory>();
        factory.Setup(f => f.CreateScope()).Returns(scope.Object);
        _scopeFactory = factory.Object;
    }

    private static MediaHub BuildHub(IServiceScopeFactory scopeFactory, Guid userId,
        out Mock<IGroupManager> groups)
    {
        groups = new Mock<IGroupManager>();
        var context = new Mock<HubCallerContext>();
        context.Setup(c => c.ConnectionId).Returns("conn-1");
        // Media-token shaped principal: identity (sub) WITHOUT a role claim.
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            new[] { new Claim(ClaimTypes.NameIdentifier, userId.ToString()) }, "TestAuth"));
        context.Setup(c => c.User).Returns(principal);

        return new MediaHub(NullLogger<MediaHub>.Instance, scopeFactory)
        {
            Context = context.Object,
            Groups = groups.Object,
        };
    }

    private async Task<Guid> SeedUserAsync(UserRole role, bool deleted = false)
    {
        var user = new User
        {
            Id = Guid.NewGuid(),
            Username = $"u-{Guid.NewGuid():N}",
            PasswordHash = "x",
            Role = role,
            IsDeleted = deleted,
        };
        _db.Users.Add(user);
        await _db.SaveChangesAsync();
        return user.Id;
    }

    [Fact]
    public async Task OnConnected_AdminUser_JoinsScanAdminsGroup_ViaDbLookup()
    {
        var adminId = await SeedUserAsync(UserRole.Admin);
        var hub = BuildHub(_scopeFactory, adminId, out var groups);

        await hub.OnConnectedAsync();

        groups.Verify(g => g.AddToGroupAsync("conn-1", MediaHub.AdminGroup, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task OnConnected_RegularUser_DoesNotJoinScanAdminsGroup()
    {
        var userId = await SeedUserAsync(UserRole.User);
        var hub = BuildHub(_scopeFactory, userId, out var groups);

        await hub.OnConnectedAsync();

        groups.Verify(g => g.AddToGroupAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task OnConnected_SoftDeletedAdmin_DoesNotJoinScanAdminsGroup()
    {
        var deletedAdminId = await SeedUserAsync(UserRole.Admin, deleted: true);
        var hub = BuildHub(_scopeFactory, deletedAdminId, out var groups);

        await hub.OnConnectedAsync();

        groups.Verify(g => g.AddToGroupAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task OnConnected_UnknownUserId_DoesNotThrow_AndJoinsNothing()
    {
        var hub = BuildHub(_scopeFactory, Guid.NewGuid(), out var groups);

        await hub.OnConnectedAsync();

        groups.Verify(g => g.AddToGroupAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
