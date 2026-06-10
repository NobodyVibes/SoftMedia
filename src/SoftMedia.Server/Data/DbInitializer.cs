using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using SoftMedia.Server.Models;
using SoftMedia.Server.Services.Identity;
using SoftMedia.Server.Services.Infrastructure;
using SoftMedia.Server.Services.Media;
using SoftMedia.Server.Services.Scanning;
using SoftMedia.Server.Services.Transcoding;

namespace SoftMedia.Server.Data;

public static class DbInitializer
{
    public static async Task InitializeAsync(IServiceProvider serviceProvider)
    {
        using var scope = serviceProvider.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var passwordHasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();
        var logger = scope.ServiceProvider.GetRequiredService<ILogger<AppDbContext>>();
        var settingsService = scope.ServiceProvider.GetRequiredService<ISettingsService>();

        // Apply any pending migrations (creates database if not exists)
        await context.Database.MigrateAsync();

        // Initialize default settings
        await settingsService.InitializeDefaultsAsync();

        var env = scope.ServiceProvider.GetRequiredService<IHostEnvironment>();

        // Check if admin user exists
        var existingAdmin = await context.Users.FirstOrDefaultAsync(u => u.Username == "admin");
        if (existingAdmin != null)
        {
            // Legacy hardening (security audit C1): older installs shipped a fixed default
            // password ("admin123"). If an admin row is STILL using it, rotate to a random
            // password and force a change — leaving it intact would let anyone log in with
            // the universally-known default and then complete the password change
            // themselves. Installs that already moved off the default are left untouched.
            if (passwordHasher.VerifyPassword("admin123", existingAdmin.PasswordHash))
            {
                var rotated = GenerateStrongPassword();
                existingAdmin.PasswordHash = passwordHasher.HashPassword(rotated);
                existingAdmin.MustChangePassword = true;
                await context.SaveChangesAsync();
                AnnounceAdminPassword(logger, rotated,
                    "The known default admin password was detected and has been rotated for security.");
            }
            else
            {
                logger.LogInformation("Admin user already exists.");
            }
        }
        else
        {
            logger.LogInformation("Seeding default admin user...");

            // Generate a unique random password per install instead of a hardcoded
            // default (security audit C1). Surfaced once via the log + ADMIN_CREDENTIALS.txt;
            // MustChangePassword is enforced server-side so it must be changed on first login.
            var initialPassword = GenerateStrongPassword();
            var adminUser = new User
            {
                Username = "admin",
                PasswordHash = passwordHasher.HashPassword(initialPassword),
                Role = UserRole.Admin,
                IsApproved = true,
                ContentRatings = "{}",
                FirstName = "Admin",
                LastName = "User",
                MustChangePassword = true
            };

            context.Users.Add(adminUser);
            await context.SaveChangesAsync();
            AnnounceAdminPassword(logger, initialPassword,
                "A default 'admin' account was created with the random password below.");
        }

        // Seed Test Library — DEV/TEST ONLY. This points at a nonexistent C:\TestMedia
        // and writes a dummy media file; it must never ship to a production database
        // (security audit, WS-1/T1.5). Gated out of Production.
        if (!env.IsProduction() && !await context.Libraries.AnyAsync())
        {
            logger.LogInformation("Seeding test library...");
            var library = new Library
            {
                Id = Guid.NewGuid(),
                Name = "Test Movies",
                Type = LibraryType.Movie,
                Paths = new List<string> { "C:\\TestMedia" }
            };
            context.Libraries.Add(library);
            await context.SaveChangesAsync();

            // Seed Test Media Item
            // Create a dummy file first
            var dummyPath = Path.Combine(Directory.GetCurrentDirectory(), "test_media.mkv");
            if (!File.Exists(dummyPath))
            {
                await File.WriteAllTextAsync(dummyPath, "dummy content");
            }

            var mediaItem = new MediaItem
            {
                Id = Guid.NewGuid(),
                LibraryId = library.Id,
                Title = "Test Movie",
                Path = dummyPath,
                Size = 1024,
                DateAdded = DateTime.UtcNow,
                DateModified = DateTime.UtcNow,
                Container = "mkv"
            };
            context.MediaItems.Add(mediaItem);
            await context.SaveChangesAsync();
            logger.LogInformation("Test library and media item seeded.");
        }
    }

    /// <summary>Generates a URL-safe random password with ~128 bits of entropy.</summary>
    private static string GenerateStrongPassword()
    {
        var bytes = RandomNumberGenerator.GetBytes(18);
        return Convert.ToBase64String(bytes)
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');
    }

    /// <summary>
    /// Surfaces a generated admin password exactly once: prominently in the log AND
    /// written to ADMIN_CREDENTIALS.txt in the working directory (more discoverable than
    /// console output under Docker). The operator must change it on first sign-in — the
    /// MustChangePassword flag is enforced server-side in the request pipeline.
    /// </summary>
    private static void AnnounceAdminPassword(ILogger logger, string password, string reason)
    {
        logger.LogWarning(
            "\n==================================================================\n" +
            " SOFTMEDIA INITIAL ADMIN CREDENTIALS\n" +
            " {Reason}\n" +
            "   username: admin\n" +
            "   password: {Password}\n" +
            " You will be required to change this password on first sign-in.\n" +
            "==================================================================",
            reason, password);
        try
        {
            var path = Path.Combine(Directory.GetCurrentDirectory(), "ADMIN_CREDENTIALS.txt");
            File.WriteAllText(path,
                "SoftMedia initial admin credentials\n" +
                reason + "\n" +
                "username: admin\n" +
                "password: " + password + "\n" +
                "Change this password on first sign-in, then delete this file.\n");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not write ADMIN_CREDENTIALS.txt; use the password from the log above.");
        }
    }
}
