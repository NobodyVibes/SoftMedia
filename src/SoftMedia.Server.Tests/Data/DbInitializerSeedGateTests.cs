using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SoftMedia.Server.Data;
using SoftMedia.Server.Models;
using SoftMedia.Server.Services.Identity;
using SoftMedia.Server.Services.Infrastructure;
using Xunit;

namespace SoftMedia.Server.Tests.Data;

/// <summary>
/// SR-WI-065 — the fake "Test Movies" library (nonexistent C:\TestMedia + dummy
/// media file) previously seeded in EVERY non-Production environment; it is now
/// gated to Development ONLY, so Staging/Testing/custom environments never get
/// fabricated data.
/// </summary>
public class DbInitializerSeedGateTests
{
    private static ServiceProvider BuildProvider(SqliteConnection connection, string environmentName)
    {
        var services = new ServiceCollection();
        services.AddDbContext<AppDbContext>(o => o.UseSqlite(connection));

        var hasher = new Mock<IPasswordHasher>();
        hasher.Setup(h => h.HashPassword(It.IsAny<string>())).Returns("hashed");
        hasher.Setup(h => h.VerifyPassword(It.IsAny<string>(), It.IsAny<string>())).Returns(false);
        services.AddSingleton(hasher.Object);

        var settings = new Mock<ISettingsService>();
        settings.Setup(s => s.InitializeDefaultsAsync()).Returns(Task.CompletedTask);
        services.AddSingleton(settings.Object);

        var env = new Mock<IHostEnvironment>();
        env.SetupGet(e => e.EnvironmentName).Returns(environmentName);
        services.AddSingleton(env.Object);

        services.AddSingleton<ILogger<AppDbContext>>(NullLogger<AppDbContext>.Instance);
        return services.BuildServiceProvider();
    }

    private static async Task<bool> RunAndReportSeedAsync(string environmentName)
    {
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        var provider = BuildProvider(connection, environmentName);

        // Migrate up front and pre-seed a non-default-password admin so the
        // initializer's admin branch stays quiet (no ADMIN_CREDENTIALS.txt side
        // effect in the test working directory).
        using (var scope = provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.Database.MigrateAsync();
            db.Users.Add(new User
            {
                Username = "admin",
                PasswordHash = "not-the-default",
                Role = UserRole.Admin,
                IsApproved = true,
                ContentRatings = "{}",
                FirstName = "A",
                LastName = "U",
            });
            await db.SaveChangesAsync();
        }

        try
        {
            await DbInitializer.InitializeAsync(provider);

            using var checkScope = provider.CreateScope();
            var check = checkScope.ServiceProvider.GetRequiredService<AppDbContext>();
            return await check.Libraries.AnyAsync(l => l.Name == "Test Movies");
        }
        finally
        {
            // The Development seed writes a dummy media file into the CWD; keep
            // the test bin directory clean either way.
            var dummy = Path.Combine(Directory.GetCurrentDirectory(), "test_media.mkv");
            if (File.Exists(dummy)) File.Delete(dummy);
            await provider.DisposeAsync();
        }
    }

    [Fact]
    public async Task Development_SeedsTheTestLibrary()
    {
        Assert.True(await RunAndReportSeedAsync("Development"));
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    [InlineData("Testing")]
    public async Task NonDevelopmentEnvironments_DoNotSeedTheTestLibrary(string environmentName)
    {
        Assert.False(await RunAndReportSeedAsync(environmentName));
    }
}
