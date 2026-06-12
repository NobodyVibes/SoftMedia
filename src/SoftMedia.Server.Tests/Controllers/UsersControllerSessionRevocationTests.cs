using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Moq;
using SoftMedia.Server.Controllers;
using SoftMedia.Server.Data;
using SoftMedia.Server.DTOs;
using SoftMedia.Server.Models;
using SoftMedia.Server.Services.Abstractions;
using SoftMedia.Server.Services.Identity;
using SoftMedia.Server.Services.Infrastructure;
using System.Security.Claims;
using Xunit;

namespace SoftMedia.Server.Tests.Controllers;

/// Audit wave-2 WS-3 (H-2/L-6): admin account-state mutations (password reset, ban, deny,
/// delete, un-approve) must revoke the target's refresh tokens AND remembered 2FA devices so a
/// stolen/active session can't survive the action. These lock that wiring.
public class UsersControllerSessionRevocationTests : IDisposable
{
    private readonly AppDbContext _db;
    private readonly Mock<IRefreshTokenService> _refresh = new();
    private readonly Mock<ITrustedDeviceService> _devices = new();
    private readonly Guid _adminId = Guid.NewGuid();
    private readonly Guid _targetId = Guid.NewGuid();

    public UsersControllerSessionRevocationTests()
    {
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"users-revoke-{Guid.NewGuid()}")
            .Options);
        _db.Users.AddRange(
            new User { Id = _adminId, Username = "admin", PasswordHash = "x", Role = UserRole.Admin, IsApproved = true },
            new User { Id = _targetId, Username = "target", PasswordHash = "x", Role = UserRole.User, IsApproved = true });
        _db.SaveChanges();
        _devices.Setup(d => d.RevokeAllAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync(0);
    }

    public void Dispose() => _db.Dispose();

    private UsersController NewController()
    {
        var hasher = new Mock<IPasswordHasher>();
        hasher.Setup(h => h.HashPassword(It.IsAny<string>())).Returns("hashed");
        var prefs = new Mock<IUserPreferencesService>();

        var controller = new UsersController(_db, hasher.Object, prefs.Object, _refresh.Object, _devices.Object);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                {
                    new Claim(ClaimTypes.NameIdentifier, _adminId.ToString()),
                    new Claim(ClaimTypes.Role, "Admin"),
                }, "Test")),
            },
        };
        return controller;
    }

    private void VerifyRevoked(string reason, Times times)
    {
        _refresh.Verify(r => r.RevokeAllForUserAsync(_targetId, reason, It.IsAny<CancellationToken>()), times);
        _devices.Verify(d => d.RevokeAllAsync(_targetId, It.IsAny<CancellationToken>()), times);
    }

    [Fact]
    public async Task ResetPassword_RevokesSessions()
    {
        var result = await NewController().ResetUserPassword(_targetId, new ResetUserPasswordRequest("Str0ngP@ssw0rd!"));
        Assert.IsType<OkResult>(result);
        VerifyRevoked(RefreshTokenRevocationReason.PasswordChange, Times.Once());
    }

    [Fact]
    public async Task Ban_RevokesSessions_ButUnban_DoesNot()
    {
        await NewController().BanUser(_targetId, new BanUserRequest(true));
        VerifyRevoked(RefreshTokenRevocationReason.AccountSuspended, Times.Once());

        _refresh.Invocations.Clear();
        _devices.Invocations.Clear();

        await NewController().BanUser(_targetId, new BanUserRequest(false));
        _refresh.Verify(r => r.RevokeAllForUserAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never());
        _devices.Verify(d => d.RevokeAllAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never());
    }

    [Fact]
    public async Task CreateUser_RejectsWeakPassword()
    {
        // Audit wave-2 L-7: admin CreateUser must enforce the password policy like signup/reset.
        var weak = await NewController().CreateUser(new CreateUserRequest("freshuser", "short", "User", "F", "L"));
        Assert.IsType<BadRequestObjectResult>(weak);
        Assert.DoesNotContain(_db.Users, u => u.Username == "freshuser"); // not created

        var ok = await NewController().CreateUser(new CreateUserRequest("freshuser", "Str0ngP@ssw0rd!", "User", "F", "L"));
        Assert.IsNotType<BadRequestObjectResult>(ok);
    }

    [Fact]
    public async Task Deny_And_Delete_And_Unapprove_RevokeSessions()
    {
        await NewController().DenyUser(_targetId);
        VerifyRevoked(RefreshTokenRevocationReason.AccountSuspended, Times.Once());

        _refresh.Invocations.Clear();
        _devices.Invocations.Clear();
        await NewController().DeleteUser(_targetId);
        VerifyRevoked(RefreshTokenRevocationReason.AccountSuspended, Times.Once());

        _refresh.Invocations.Clear();
        _devices.Invocations.Clear();
        await NewController().ApproveUser(_targetId, new ApproveUserRequest(false));
        VerifyRevoked(RefreshTokenRevocationReason.AccountSuspended, Times.Once());
    }
}
