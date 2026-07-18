using System.Data.Common;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Tokens;
using SoftMedia.Server.Data;
using SoftMedia.Server.Models;
using SoftMedia.Server.Models.Options;

namespace SoftMedia.Server.Tests.Helpers;

/// Shared integration-test harness. Each instance owns an isolated in-memory
/// SQLite connection and a freshly-generated JWT secret so parallel tests
/// cannot collide on the DB or forge tokens across test cases.
///
/// The production `DbInitializer` runs on startup and seeds an admin user and
/// a test library; the factory deletes the library row afterwards because it
/// points at `C:\TestMedia` which does not exist and would trip
/// `LibraryWatcher`'s `FileSystemWatcher` construction.
public class SoftMediaWebApplicationFactory : WebApplicationFactory<Program>
{
    public string JwtSecret { get; } = JwtSecretGenerator.Generate();

    // T-01 (flaky-test stabilization): a single shared OPEN connection handed to
    // every EF scope AND every background hosted service meant concurrent
    // commands/transactions collided ON THE SAME CONNECTION under parallel-run
    // CPU contention ("database is locked" 500s in unrelated requests) — a class
    // of failure busy_timeout cannot fix, since that only mediates BETWEEN
    // connections. A uniquely-named shared-cache in-memory DB lets every scope
    // open its OWN connection (Microsoft.Data.Sqlite waits on shared-cache locks
    // via unlock-notify, honoring Default Timeout); this keep-alive connection
    // pins the database for the factory's lifetime.
    private readonly string _dbConnectionString =
        $"Data Source=softmedia-it-{Guid.NewGuid():N};Mode=Memory;Cache=Shared;Default Timeout=30";
    private readonly DbConnection _keepAliveConnection;

    public SoftMediaWebApplicationFactory()
    {
        _keepAliveConnection = new SqliteConnection(_dbConnectionString);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        _keepAliveConnection.Open();

        builder.UseEnvironment("Testing");

        builder.ConfigureAppConfiguration((_, configBuilder) =>
        {
            configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["JwtSettings:Secret"] = JwtSecret,
                ["JwtSettings:Issuer"] = "SoftMediaServer",
                ["JwtSettings:Audience"] = "SoftMediaClient",
                ["JwtSettings:ExpiryMinutes"] = "15",
                ["JwtSettings:CastTokenExpiryHours"] = "9", // distinct from any default, so the cast-token TTL test proves config is read
            });
        });

        builder.ConfigureServices(services =>
        {
            // Replace the SQLite file-based registration with this factory's
            // named in-memory database. Registering the CONNECTION STRING (not a
            // connection object) is what gives each scope its own connection.
            var dbDescriptor = services.Single(d => d.ServiceType == typeof(DbContextOptions<AppDbContext>));
            services.Remove(dbDescriptor);
            services.AddDbContext<AppDbContext>(options =>
                options.UseSqlite(_dbConnectionString));
        });
    }

    /// Name of the seed library created by <see cref="DbInitializer"/>. Kept in
    /// sync with the production seed so the cleanup sentinel is greppable.
    private const string SeedLibraryName = "Test Movies";

    /// Clears the seeded test library so that <c>LibraryWatcher</c>'s
    /// <c>FileSystemWatcher</c> does not try to attach to the nonexistent
    /// <c>C:\TestMedia</c> path baked into the production seed.
    public async Task ResetSeedNoiseAsync()
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var seeded = await db.Libraries.Where(l => l.Name == SeedLibraryName).ToListAsync();
        db.Libraries.RemoveRange(seeded);
        await db.SaveChangesAsync();
    }

    public async Task<User> SeedUserAsync(
        string username,
        string password = "TestPass!1",
        UserRole role = UserRole.User,
        bool approved = true)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var hasher = scope.ServiceProvider.GetRequiredService<SoftMedia.Server.Services.Identity.IPasswordHasher>();

        var user = new User
        {
            Id = Guid.NewGuid(),
            Username = username,
            PasswordHash = hasher.HashPassword(password),
            Role = role,
            IsApproved = approved,
            CreatedAt = DateTime.UtcNow,
            FirstName = "T", LastName = "T",
            ContentRatings = "{}",
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user;
    }

    public async Task<T> WithDbAsync<T>(Func<AppDbContext, Task<T>> action)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await action(db);
    }

    public async Task WithDbAsync(Func<AppDbContext, Task> action)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await action(db);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            _keepAliveConnection.Dispose();
            // Pooled connections would otherwise keep the named in-memory DB
            // alive (and its memory allocated) across the whole test run.
            SqliteConnection.ClearPool(new SqliteConnection(_dbConnectionString));
        }
    }
}
