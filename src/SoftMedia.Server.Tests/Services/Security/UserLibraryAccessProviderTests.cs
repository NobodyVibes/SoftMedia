using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SoftMedia.Server.Data;
using SoftMedia.Server.Models;
using SoftMedia.Server.Services.Security.LibraryAccess;
using Xunit;

namespace SoftMedia.Server.Tests.Services.Security;

/// Wave C — UserLibraryAccessProvider must:
///   - Return Unrestricted with no HttpContext (background scanners).
///   - Return Unrestricted for anonymous principals.
///   - Return Unrestricted for Admin role regardless of UserLibraryAccess rows.
///   - Return Unrestricted when a user has zero ACL rows (default semantics).
///   - Return AllowOnly populated with exactly the user's allowed library IDs.
///   - Cache the result on HttpContext.Items so repeat calls in the same
///     request hit the DB once.
public class UserLibraryAccessProviderTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;
    private readonly Guid _userId = Guid.NewGuid();
    private readonly Guid _adminId = Guid.NewGuid();
    private readonly Library _libA;
    private readonly Library _libB;

    public UserLibraryAccessProviderTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options;

        _libA = new Library { Id = Guid.NewGuid(), Name = "A", Type = LibraryType.Movie, Paths = new() { "/a" } };
        _libB = new Library { Id = Guid.NewGuid(), Name = "B", Type = LibraryType.Movie, Paths = new() { "/b" } };

        using var ctx = new AppDbContext(_options);
        ctx.Database.EnsureCreated();
        ctx.Libraries.AddRange(_libA, _libB);
        ctx.Users.AddRange(
            new User { Id = _userId, Username = "u", PasswordHash = "x", Role = UserRole.User, IsApproved = true },
            new User { Id = _adminId, Username = "a", PasswordHash = "x", Role = UserRole.Admin, IsApproved = true });
        ctx.SaveChanges();
    }

    public void Dispose() => _connection.Dispose();

    private sealed class FixedHttpContextAccessor : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get; set; }
    }

    private static HttpContext BuildContext(Guid? userId, UserRole? role)
    {
        var ctx = new DefaultHttpContext();
        if (userId is not null)
        {
            var claims = new List<Claim>
            {
                new(ClaimTypes.NameIdentifier, userId.Value.ToString()),
            };
            if (role is not null)
            {
                claims.Add(new Claim(ClaimTypes.Role, role.Value.ToString()));
            }
            var identity = new ClaimsIdentity(claims, "TestAuth");
            ctx.User = new ClaimsPrincipal(identity);
        }
        return ctx;
    }

    private UserLibraryAccessProvider NewProvider(HttpContext? ctx, AppDbContext db) =>
        new(new FixedHttpContextAccessor { HttpContext = ctx }, db);

    [Fact]
    public async Task NoHttpContext_ReturnsUnrestricted()
    {
        using var db = new AppDbContext(_options);
        var provider = NewProvider(ctx: null, db);

        var result = await provider.GetCurrentAsync();

        Assert.True(result.IsUnrestricted);
    }

    [Fact]
    public async Task AnonymousPrincipal_ReturnsUnrestricted()
    {
        using var db = new AppDbContext(_options);
        var provider = NewProvider(BuildContext(userId: null, role: null), db);

        var result = await provider.GetCurrentAsync();

        Assert.True(result.IsUnrestricted);
    }

    [Fact]
    public async Task AdminRole_ReturnsUnrestrictedRegardlessOfAclRows()
    {
        // Seed an admin with explicit ACL rows; admin must still bypass.
        using (var seed = new AppDbContext(_options))
        {
            seed.UserLibraryAccess.Add(new UserLibraryAccess
            {
                UserId = _adminId,
                LibraryId = _libA.Id
            });
            await seed.SaveChangesAsync();
        }

        using var db = new AppDbContext(_options);
        var provider = NewProvider(BuildContext(_adminId, UserRole.Admin), db);

        var result = await provider.GetCurrentAsync();

        Assert.True(result.IsUnrestricted);
    }

    [Fact]
    public async Task UserWithNoRows_ReturnsUnrestricted()
    {
        using var db = new AppDbContext(_options);
        var provider = NewProvider(BuildContext(_userId, UserRole.User), db);

        var result = await provider.GetCurrentAsync();

        Assert.True(result.IsUnrestricted);
    }

    [Fact]
    public async Task UserWithRows_ReturnsAllowOnlyContainingExactlyThoseIds()
    {
        using (var seed = new AppDbContext(_options))
        {
            seed.UserLibraryAccess.Add(new UserLibraryAccess { UserId = _userId, LibraryId = _libA.Id });
            await seed.SaveChangesAsync();
        }

        using var db = new AppDbContext(_options);
        var provider = NewProvider(BuildContext(_userId, UserRole.User), db);

        var result = await provider.GetCurrentAsync();

        Assert.False(result.IsUnrestricted);
        Assert.Single(result.AllowedLibraryIds);
        Assert.Contains(_libA.Id, result.AllowedLibraryIds);
        Assert.DoesNotContain(_libB.Id, result.AllowedLibraryIds);
    }

    [Fact]
    public async Task RepeatedCallsInSameRequest_HitDbOnce_ViaHttpContextCache()
    {
        using (var seed = new AppDbContext(_options))
        {
            seed.UserLibraryAccess.Add(new UserLibraryAccess { UserId = _userId, LibraryId = _libA.Id });
            await seed.SaveChangesAsync();
        }

        using var db = new AppDbContext(_options);
        var ctx = BuildContext(_userId, UserRole.User);
        var provider = NewProvider(ctx, db);

        var first = await provider.GetCurrentAsync();
        // Mutate DB after the first call. If caching works, the second
        // call returns the same value as the first — proving it didn't
        // re-hit the DB.
        using (var mutate = new AppDbContext(_options))
        {
            mutate.UserLibraryAccess.RemoveRange(mutate.UserLibraryAccess);
            await mutate.SaveChangesAsync();
        }
        var second = await provider.GetCurrentAsync();

        Assert.Equal(first.IsUnrestricted, second.IsUnrestricted);
        Assert.Equal(first.AllowedLibraryIds.Count, second.AllowedLibraryIds.Count);
    }

    [Fact]
    public async Task MalformedSubClaim_FailsOpenToUnrestricted()
    {
        // Bearer middleware already validated signature; reaching here with
        // a non-Guid sub means an internal bug. Provider must fail open so
        // a real user is not locked out.
        var ctx = new DefaultHttpContext();
        var identity = new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, "not-a-guid"),
            new Claim(ClaimTypes.Role, UserRole.User.ToString()),
        }, "TestAuth");
        ctx.User = new ClaimsPrincipal(identity);

        using var db = new AppDbContext(_options);
        var provider = NewProvider(ctx, db);

        var result = await provider.GetCurrentAsync();

        Assert.True(result.IsUnrestricted);
    }
}
