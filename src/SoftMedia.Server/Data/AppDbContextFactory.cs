using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace SoftMedia.Server.Data;

/// <summary>
/// Design-time factory for AppDbContext. Used by EF Core tools (dotnet ef migrations)
/// to create a DbContext without starting the full application host.
/// This avoids DI resolution issues that occur when the host has complex service dependencies.
/// </summary>
public class AppDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<AppDbContext>();
        optionsBuilder.UseSqlite("Data Source=softmedia.db");

        return new AppDbContext(optionsBuilder.Options);
    }
}
