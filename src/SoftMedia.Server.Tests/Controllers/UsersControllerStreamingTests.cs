using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Moq;
using SoftMedia.Server.Controllers;
using SoftMedia.Server.Data;
using SoftMedia.Server.DTOs;
using SoftMedia.Server.Models;
using SoftMedia.Server.Services.Identity;
using SoftMedia.Server.Services.Infrastructure;
using Xunit;

namespace SoftMedia.Server.Tests.Controllers;

/// R-WI-009 — PUT /api/v1/users/{id}/streaming sets the per-user bitrate cap (kbps; 0 = unlimited).
/// The cap was enforced since P1-WI-003 but previously settable only by direct DB edit. The
/// class-level [Authorize(Roles="Admin")] is enforced by the framework at runtime; these focus on
/// the action logic (validation, clamping, persistence, and exposure via GET).
public class UsersControllerStreamingTests : IDisposable
{
    private readonly AppDbContext _context;
    private readonly Guid _userId = Guid.NewGuid();

    public UsersControllerStreamingTests()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"users-streaming-{Guid.NewGuid()}")
            .Options;
        _context = new AppDbContext(options);
        _context.Users.Add(new User { Id = _userId, Username = "u", PasswordHash = "x", Role = UserRole.User, IsApproved = true });
        _context.SaveChanges();
    }

    public void Dispose() => _context.Dispose();

    private UsersController NewController() =>
        new(_context, Mock.Of<IPasswordHasher>(), Mock.Of<IUserPreferencesService>(),
            Mock.Of<SoftMedia.Server.Services.Abstractions.IRefreshTokenService>(),
            Mock.Of<ITrustedDeviceService>(),
            new SoftMedia.Server.Services.Identity.UserEligibilityCache());

    [Fact]
    public async Task UpdateUserStreaming_SetsCap()
    {
        var result = await NewController().UpdateUserStreaming(_userId, new UpdateUserStreamingRequest(4000));

        Assert.IsType<OkResult>(result);
        Assert.Equal(4000, (await _context.Users.FindAsync(_userId))!.MaxStreamBitrateKbps);
    }

    [Fact]
    public async Task UpdateUserStreaming_Zero_MeansUnlimited()
    {
        await NewController().UpdateUserStreaming(_userId, new UpdateUserStreamingRequest(4000));
        var result = await NewController().UpdateUserStreaming(_userId, new UpdateUserStreamingRequest(0));

        Assert.IsType<OkResult>(result);
        Assert.Equal(0, (await _context.Users.FindAsync(_userId))!.MaxStreamBitrateKbps);
    }

    [Fact]
    public async Task UpdateUserStreaming_Negative_Returns400()
    {
        var result = await NewController().UpdateUserStreaming(_userId, new UpdateUserStreamingRequest(-1));

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task UpdateUserStreaming_UnknownUser_Returns404()
    {
        var result = await NewController().UpdateUserStreaming(Guid.NewGuid(), new UpdateUserStreamingRequest(4000));

        Assert.IsType<NotFoundObjectResult>(result);
    }

    [Fact]
    public async Task UpdateUserStreaming_ClampsAbsurdValue()
    {
        await NewController().UpdateUserStreaming(_userId, new UpdateUserStreamingRequest(5_000_000));

        Assert.Equal(100_000, (await _context.Users.FindAsync(_userId))!.MaxStreamBitrateKbps);
    }

    [Fact]
    public async Task GetUsers_ExposesTheCap()
    {
        await NewController().UpdateUserStreaming(_userId, new UpdateUserStreamingRequest(3000));

        var result = await NewController().GetUsers();

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var users = Assert.IsAssignableFrom<IEnumerable<UserDto>>(ok.Value);
        var dto = Assert.Single(users, u => u.Id == _userId);
        Assert.Equal(3000, dto.MaxStreamBitrateKbps);
    }

    // ---------- QS-WI-002: remote bitrate variant + resolution ceiling ----------

    [Fact]
    public async Task UpdateUserStreaming_SetsRemoteAndResolutionLimits()
    {
        var result = await NewController().UpdateUserStreaming(_userId,
            new UpdateUserStreamingRequest(3000, RemoteMaxStreamBitrateKbps: 8000, MaxStreamResolution: 1080));

        Assert.IsType<OkResult>(result);
        var user = (await _context.Users.FindAsync(_userId))!;
        Assert.Equal(3000, user.MaxStreamBitrateKbps);
        Assert.Equal(8000, user.RemoteMaxStreamBitrateKbps);
        Assert.Equal(1080, user.MaxStreamResolution);
    }

    [Fact]
    public async Task UpdateUserStreaming_ZeroOrOmitted_ClearsRemoteAndResolution()
    {
        await NewController().UpdateUserStreaming(_userId,
            new UpdateUserStreamingRequest(3000, 8000, 1080));

        // Explicit zeros clear; a request omitting the new fields (older client) clears too —
        // the PUT is a full replace of the streaming-limits trio, matching the base cap.
        var result = await NewController().UpdateUserStreaming(_userId,
            new UpdateUserStreamingRequest(3000, 0, 0));

        Assert.IsType<OkResult>(result);
        var user = (await _context.Users.FindAsync(_userId))!;
        Assert.Null(user.RemoteMaxStreamBitrateKbps);
        Assert.Null(user.MaxStreamResolution);
    }

    [Fact]
    public async Task UpdateUserStreaming_NegativeRemote_Returns400()
    {
        var result = await NewController().UpdateUserStreaming(_userId,
            new UpdateUserStreamingRequest(0, RemoteMaxStreamBitrateKbps: -5));

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task UpdateUserStreaming_UnknownResolution_Returns400()
    {
        var result = await NewController().UpdateUserStreaming(_userId,
            new UpdateUserStreamingRequest(0, MaxStreamResolution: 999));

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task UpdateUserStreaming_ClampsAbsurdRemoteValue()
    {
        await NewController().UpdateUserStreaming(_userId,
            new UpdateUserStreamingRequest(0, RemoteMaxStreamBitrateKbps: 5_000_000));

        Assert.Equal(100_000, (await _context.Users.FindAsync(_userId))!.RemoteMaxStreamBitrateKbps);
    }

    [Fact]
    public async Task GetUsers_ExposesTheNewLimits()
    {
        await NewController().UpdateUserStreaming(_userId,
            new UpdateUserStreamingRequest(3000, 8000, 2160));

        var result = await NewController().GetUsers();

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var users = Assert.IsAssignableFrom<IEnumerable<UserDto>>(ok.Value);
        var dto = Assert.Single(users, u => u.Id == _userId);
        Assert.Equal(8000, dto.RemoteMaxStreamBitrateKbps);
        Assert.Equal(2160, dto.MaxStreamResolution);
    }
}
