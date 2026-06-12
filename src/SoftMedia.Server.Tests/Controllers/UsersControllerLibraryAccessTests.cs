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

/// Wave C — coverage for GET/PUT /api/v1/users/{id}/library-access. The
/// class-level [Authorize(Roles = "Admin")] is enforced by the framework
/// at runtime; controller-level unit tests focus on the action logic
/// (validation, mutation, idempotency).
public class UsersControllerLibraryAccessTests : IDisposable
{
    private readonly AppDbContext _context;
    private readonly Guid _userId = Guid.NewGuid();
    private readonly Guid _adminId = Guid.NewGuid();
    private readonly Library _libA;
    private readonly Library _libB;

    public UsersControllerLibraryAccessTests()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"users-libacl-{Guid.NewGuid()}")
            .Options;
        _context = new AppDbContext(options);

        _libA = new Library { Id = Guid.NewGuid(), Name = "A", Type = LibraryType.Movie, Paths = new() { "/a" } };
        _libB = new Library { Id = Guid.NewGuid(), Name = "B", Type = LibraryType.Movie, Paths = new() { "/b" } };
        _context.Libraries.AddRange(_libA, _libB);
        _context.Users.AddRange(
            new User { Id = _userId, Username = "u", PasswordHash = "x", Role = UserRole.User, IsApproved = true },
            new User { Id = _adminId, Username = "a", PasswordHash = "x", Role = UserRole.Admin, IsApproved = true });
        _context.SaveChanges();
    }

    public void Dispose() => _context.Dispose();

    private UsersController NewController() =>
        new(_context, Mock.Of<IPasswordHasher>(), Mock.Of<IUserPreferencesService>(),
            Mock.Of<SoftMedia.Server.Services.Abstractions.IRefreshTokenService>(),
            Mock.Of<SoftMedia.Server.Services.Identity.ITrustedDeviceService>());

    [Fact]
    public async Task GetUserLibraryAccess_NoRows_ReturnsEmptyArray()
    {
        var result = await NewController().GetUserLibraryAccess(_userId);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var ids = Assert.IsType<List<Guid>>(ok.Value);
        Assert.Empty(ids);
    }

    [Fact]
    public async Task GetUserLibraryAccess_UnknownUser_Returns404()
    {
        var result = await NewController().GetUserLibraryAccess(Guid.NewGuid());
        Assert.IsType<NotFoundObjectResult>(result.Result);
    }

    [Fact]
    public async Task GetUserLibraryAccess_WithRows_ReturnsThoseIds()
    {
        _context.UserLibraryAccess.Add(new UserLibraryAccess { UserId = _userId, LibraryId = _libA.Id });
        await _context.SaveChangesAsync();

        var result = await NewController().GetUserLibraryAccess(_userId);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var ids = Assert.IsType<List<Guid>>(ok.Value);
        Assert.Single(ids);
        Assert.Equal(_libA.Id, ids[0]);
    }

    [Fact]
    public async Task SetUserLibraryAccess_PopulatesAndPersists()
    {
        var result = await NewController().SetUserLibraryAccess(_userId, new SetLibraryAccessRequest
        {
            LibraryIds = new List<Guid> { _libA.Id, _libB.Id }
        });

        Assert.IsType<OkResult>(result);
        var rows = await _context.UserLibraryAccess.Where(a => a.UserId == _userId).ToListAsync();
        Assert.Equal(2, rows.Count);
    }

    [Fact]
    public async Task SetUserLibraryAccess_EmptyList_ClearsExistingRows()
    {
        // Pre-seed two rows; the empty payload should remove them all.
        _context.UserLibraryAccess.AddRange(
            new UserLibraryAccess { UserId = _userId, LibraryId = _libA.Id },
            new UserLibraryAccess { UserId = _userId, LibraryId = _libB.Id });
        await _context.SaveChangesAsync();

        var result = await NewController().SetUserLibraryAccess(_userId, new SetLibraryAccessRequest
        {
            LibraryIds = new List<Guid>()
        });

        Assert.IsType<OkResult>(result);
        Assert.Empty(await _context.UserLibraryAccess.Where(a => a.UserId == _userId).ToListAsync());
    }

    [Fact]
    public async Task SetUserLibraryAccess_NullList_ClearsExistingRows()
    {
        // Treat a missing list the same as an empty list — keeps the contract
        // forgiving for clients that omit the field entirely.
        _context.UserLibraryAccess.Add(new UserLibraryAccess { UserId = _userId, LibraryId = _libA.Id });
        await _context.SaveChangesAsync();

        var result = await NewController().SetUserLibraryAccess(_userId, new SetLibraryAccessRequest
        {
            LibraryIds = null
        });

        Assert.IsType<OkResult>(result);
        Assert.Empty(await _context.UserLibraryAccess.Where(a => a.UserId == _userId).ToListAsync());
    }

    [Fact]
    public async Task SetUserLibraryAccess_TargetingAdmin_ReturnsBadRequest()
    {
        var result = await NewController().SetUserLibraryAccess(_adminId, new SetLibraryAccessRequest
        {
            LibraryIds = new List<Guid> { _libA.Id }
        });

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Empty(await _context.UserLibraryAccess.Where(a => a.UserId == _adminId).ToListAsync());
    }

    [Fact]
    public async Task SetUserLibraryAccess_UnknownLibraryId_RejectsAndDoesNotMutate()
    {
        // Pre-seed a row so we can assert the request didn't half-apply.
        _context.UserLibraryAccess.Add(new UserLibraryAccess { UserId = _userId, LibraryId = _libA.Id });
        await _context.SaveChangesAsync();

        var bogus = Guid.NewGuid();
        var result = await NewController().SetUserLibraryAccess(_userId, new SetLibraryAccessRequest
        {
            LibraryIds = new List<Guid> { _libB.Id, bogus }
        });

        Assert.IsType<BadRequestObjectResult>(result);

        // Original row still present — the validation failure happens before
        // the RemoveRange / AddRange transaction commits.
        var rows = await _context.UserLibraryAccess.Where(a => a.UserId == _userId).ToListAsync();
        Assert.Single(rows);
        Assert.Equal(_libA.Id, rows[0].LibraryId);
    }

    [Fact]
    public async Task SetUserLibraryAccess_UnknownUser_Returns404()
    {
        var result = await NewController().SetUserLibraryAccess(Guid.NewGuid(), new SetLibraryAccessRequest
        {
            LibraryIds = new List<Guid> { _libA.Id }
        });

        Assert.IsType<NotFoundObjectResult>(result);
    }

    [Fact]
    public async Task SetUserLibraryAccess_DuplicateLibraryIds_StoredOnce()
    {
        var result = await NewController().SetUserLibraryAccess(_userId, new SetLibraryAccessRequest
        {
            LibraryIds = new List<Guid> { _libA.Id, _libA.Id, _libA.Id }
        });

        Assert.IsType<OkResult>(result);
        var rows = await _context.UserLibraryAccess.Where(a => a.UserId == _userId).ToListAsync();
        Assert.Single(rows);
    }

    [Fact]
    public async Task SetUserLibraryAccess_ReplacesExistingRows()
    {
        // Start with libA; replace with libB.
        _context.UserLibraryAccess.Add(new UserLibraryAccess { UserId = _userId, LibraryId = _libA.Id });
        await _context.SaveChangesAsync();

        var result = await NewController().SetUserLibraryAccess(_userId, new SetLibraryAccessRequest
        {
            LibraryIds = new List<Guid> { _libB.Id }
        });

        Assert.IsType<OkResult>(result);
        var rows = await _context.UserLibraryAccess.Where(a => a.UserId == _userId).ToListAsync();
        Assert.Single(rows);
        Assert.Equal(_libB.Id, rows[0].LibraryId);
    }
}
