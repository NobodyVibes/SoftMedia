using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using SoftMedia.Server.Data;
using SoftMedia.Server.Extensions;
using SoftMedia.Server.Hubs;
using SoftMedia.Server.Models.Options;
using System.Net;
using System.Text;

// --generate-jwt-secret: print a fresh URL-safe 64-byte secret and exit.
// Runs before CreateBuilder so it does not require valid config to execute.
if (args.Contains("--generate-jwt-secret"))
{
    Console.WriteLine(JwtSecretGenerator.Generate());
    return;
}

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddMemoryCache();

// Forwarded Headers — required when SoftMedia runs behind a reverse proxy
// (Caddy, nginx, Tailscale Funnel, etc., the deployments recommended by
// SDD §6.1). Without this, HttpContext.Connection.RemoteIpAddress is the
// proxy's loopback address, which collapses the per-IP rate limiter
// (ServiceCollectionExtensions.cs AuthRateLimitPolicy) into a single
// shared bucket. See docs/user-docs/reverse-proxy.md for the operator
// configuration of `ForwardedHeaders:TrustedProxies` /
// `ForwardedHeaders:TrustedProxyNetworks`.
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;

    // Microsoft's defaults include loopback automatically, but we clear and
    // re-add to make the trust posture explicit alongside operator additions.
    options.KnownProxies.Clear();
    options.KnownNetworks.Clear();

    options.KnownProxies.Add(IPAddress.Loopback);
    options.KnownProxies.Add(IPAddress.IPv6Loopback);

    var trustedProxies = builder.Configuration
        .GetSection("ForwardedHeaders:TrustedProxies").Get<string[]>() ?? [];
    foreach (var ip in trustedProxies)
    {
        if (IPAddress.TryParse(ip, out var addr)) options.KnownProxies.Add(addr);
    }

    var trustedNetworks = builder.Configuration
        .GetSection("ForwardedHeaders:TrustedProxyNetworks").Get<string[]>() ?? [];
    foreach (var cidr in trustedNetworks)
    {
        if (TryParseCidr(cidr, out var network)) options.KnownNetworks.Add(network);
    }

    // Local function: parse "192.168.1.0/24" into a HttpOverrides IPNetwork.
    // System.Net.IPNetwork (.NET 8) is a different type and is not compatible
    // with ForwardedHeadersOptions.KnownNetworks, hence the manual split and
    // the fully-qualified return type (both namespaces are imported above and
    // expose an `IPNetwork`, so the unqualified name is ambiguous here).
    static bool TryParseCidr(string cidr, out Microsoft.AspNetCore.HttpOverrides.IPNetwork network)
    {
        network = default!;
        var parts = cidr.Split('/');
        if (parts.Length != 2) return false;
        if (!IPAddress.TryParse(parts[0], out var address)) return false;
        if (!int.TryParse(parts[1], out var prefixLength)) return false;
        var maxPrefix = address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? 128 : 32;
        if (prefixLength < 0 || prefixLength > maxPrefix) return false;
        network = new Microsoft.AspNetCore.HttpOverrides.IPNetwork(address, prefixLength);
        return true;
    }
});
builder.Services.AddResponseCompression(options =>
{
    options.EnableForHttps = true;
    options.MimeTypes = ResponseCompressionDefaults.MimeTypes.Concat(new[]
    {
        "application/json",
        "image/svg+xml"
    });
});
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlite(builder.Configuration.GetConnectionString("DefaultConnection")));

// Register Application Services via Extensions
builder.Services.AddIdentityServices(builder.Configuration);
builder.Services.AddSecurityServices();
builder.Services.AddMediaServices();
builder.Services.AddBackgroundServices();



// SignalR for real-time updates
builder.Services.AddSignalR();

// API Configuration
// Audit L6: reflect-ANY-origin together with AllowCredentials is the CORS spec's forbidden
// combination and a session-takeover footgun if it ever ships to production. Honour the flag
// ONLY in Development; in any other environment it is ignored (and we say so loudly), so the
// policy falls back to the explicit Cors:AllowedOrigins allowlist.
var corsAllowAnyOriginRequested = builder.Configuration.GetValue<bool>("Cors:AllowAnyOriginForLAN");
var corsAllowAnyOrigin = corsAllowAnyOriginRequested && builder.Environment.IsDevelopment();
if (corsAllowAnyOriginRequested && !corsAllowAnyOrigin)
{
    Console.Error.WriteLine(
        "[WARN] Cors:AllowAnyOriginForLAN=true is IGNORED outside Development — credentialed " +
        "wildcard CORS is unsafe in production. Configure Cors:AllowedOrigins instead.");
}
else if (corsAllowAnyOrigin)
{
    Console.Error.WriteLine(
        "[WARN] Cors:AllowAnyOriginForLAN=true (Development only) — credentialed CORS is wide open. " +
        "This is the Vite dev-proxy default; it can never take effect in production.");
}

builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowFrontend", policy =>
    {
        if (corsAllowAnyOrigin)
        {
            policy.SetIsOriginAllowed(_ => true)
                  .AllowAnyHeader()
                  .AllowAnyMethod()
                  .AllowCredentials();
        }
        else
        {
            var origins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
            policy.WithOrigins(origins)
                  .AllowAnyHeader()
                  .AllowAnyMethod()
                  .AllowCredentials();
        }
    });
});

builder.Services.AddRateLimitingPolicies();

builder.Services.AddControllers()
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
    });

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo { Title = "SoftMedia API", Version = "v1" });
    c.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Description = "JWT Authorization header using the Bearer scheme. Example: \"Authorization: Bearer {token}\"",
        Name = "Authorization",
        In = ParameterLocation.Header,
        Type = SecuritySchemeType.ApiKey,
        Scheme = "Bearer"
    });
    c.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        {
            new OpenApiSecurityScheme
            {
                Reference = new OpenApiReference
                {
                    Type = ReferenceType.SecurityScheme,
                    Id = "Bearer"
                }
            },
            Array.Empty<string>()
        }
    });
});

var app = builder.Build();

// Validate JwtSettings:Secret AFTER Build() so any test-time configuration
// overrides (WebApplicationFactory's ConfigureAppConfiguration runs during
// Build) are visible, and BEFORE Run() so we never start Kestrel / hosted
// services with a missing secret. This avoids the parallel-startup race
// that an IHostedService validator suffered against Kestrel.BindAsync.
// `dotnet ef` tooling reaches this code too — devs must configure
// JwtSettings:Secret (via user-secrets or env var) before running
// migrations. See docs/user-guide/configuration.md.
var jwtValidation = JwtOptionsValidator.Validate(app.Configuration);
if (!jwtValidation.IsValid)
{
    Console.Error.WriteLine($"[FATAL] {jwtValidation.ErrorMessage}");
    Environment.ExitCode = 1;
    return;
}

// Configure the HTTP request pipeline.

// UseForwardedHeaders MUST run before any middleware that reads the client
// IP (rate limiter, audit logging) or scheme (CORS, cookie Secure flag).
// Placed first so RemoteIpAddress / Request.Scheme reflect the real client
// for every downstream component. Trust posture is configured at startup
// in builder.Services.Configure<ForwardedHeadersOptions>.
app.UseForwardedHeaders();

// Security response headers (audit H3 Referer leak + L7 hardening + WS-13 CSP). Early so it covers
// the API, the static SPA, and error responses alike. The CSP ships REPORT-ONLY unless the
// operator opts into enforcement via Security:EnforceCsp (default false) — so it can't white-screen
// the SPA before a live reader/player/casting/SignalR run confirms the policy is clean.
app.UseSecurityHeaders(app.Configuration.GetValue<bool>("Security:EnforceCsp"));

// Audit L8: optional HTTP->HTTPS redirect. OFF by default so HTTP-only LAN deployments and
// TLS-terminating reverse proxies (which forward plain HTTP with X-Forwarded-Proto=https) are
// not broken. Operators terminating TLS at the app can enable Security:ForceHttpsRedirect.
if (app.Configuration.GetValue<bool>("Security:ForceHttpsRedirect"))
{
    app.UseHttpsRedirection();
}

// NR-WI-007: the API docs serve in EVERY environment, gated at request time by the
// EnableApiDocs setting (default on; toggling needs no restart). Development always
// serves them regardless of the setting.
app.UseWhen(
    ctx => ctx.Request.Path.StartsWithSegments("/swagger"),
    branch => branch.Use(async (ctx, next) =>
    {
        if (!ctx.RequestServices.GetRequiredService<IWebHostEnvironment>().IsDevelopment())
        {
            var settings = ctx.RequestServices.GetRequiredService<SoftMedia.Server.Services.Infrastructure.ISettingsService>();
            var enabled = await settings.GetSettingAsync("EnableApiDocs", "true");
            if (!string.Equals(enabled, "true", StringComparison.OrdinalIgnoreCase))
            {
                ctx.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }
        }
        await next();
    }));
app.UseSwagger();
app.UseSwaggerUI();

app.UseCors("AllowFrontend");
app.UseResponseCompression();

app.UseAuthentication();
app.UseAuthorization();

// Audit wave-2 L-19: UseRateLimiter MUST run AFTER UseAuthentication so the per-user partitions
// (e.g. the image-proxy policy keyed on the user id) see the authenticated principal. Previously
// it ran before authentication, so User was always null and every per-user policy silently
// collapsed to a per-IP bucket. The auth/login policy is per-IP and [AllowAnonymous], so it is
// unaffected by the move.
app.UseRateLimiter();

// Security gate (audit C1): a principal flagged "must change password" may reach ONLY
// the password-change / logout / refresh-token endpoints until they rotate the credential.
// Enforced here, server-side, so the SPA's first-login prompt cannot be bypassed by
// calling the API directly with a seeded/default credential. Runs after authentication so
// the claim is populated, and before controllers so it covers every endpoint.
app.Use(async (context, next) =>
{
    if (context.User.MustChangePassword())
    {
        var path = context.Request.Path;
        var allowed =
            path.StartsWithSegments("/api/v1/auth/change-password") ||
            path.StartsWithSegments("/api/v1/auth/logout") ||
            path.StartsWithSegments("/api/v1/auth/refresh-token");
        if (!allowed)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new
            {
                error = "password_change_required",
                message = "You must change your password before continuing."
            });
            return;
        }
    }
    await next();
});

app.UseStaticFiles();

app.MapControllers();
app.MapHub<MediaHub>("/hubs/media");

// Apply any restore staged by the admin restore endpoint on a prior run. This MUST
// run before DbInitializer opens/migrates the database. The DB path is derived from
// the same connection string EF uses (SqliteConnectionStringBuilder handles the
// "Data Source=..." form), resolved against the working directory.
{
    var connString = app.Configuration.GetConnectionString("DefaultConnection");
    if (!string.IsNullOrWhiteSpace(connString))
    {
        var dataSource = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder(connString).DataSource;
        if (!string.IsNullOrWhiteSpace(dataSource))
        {
            var dbPath = Path.GetFullPath(dataSource, Directory.GetCurrentDirectory());
            var restoreLogger = app.Services.GetRequiredService<ILogger<Program>>();
            SoftMedia.Server.Helpers.PendingRestore.Apply(dbPath, restoreLogger);
        }
    }
}

// Seed the scheduled-task registry descriptors (P1-WI-005) so the admin Background
// Tasks page lists every known task even before its first run. Services overwrite
// their row's telemetry on each cycle via IScheduledTaskRegistry.Report.
{
    var registry = app.Services.GetRequiredService<SoftMedia.Server.Services.Infrastructure.IScheduledTaskRegistry>();
    using var seed = SoftMedia.Server.Services.Infrastructure.ScheduledTaskRegistrySeeder.Seed(registry);

    // Restore last-run telemetry from the previous run so the Background Tasks card
    // doesn't show every task as "never run" after a reboot. Done before app.Run so the
    // values are present before the first dashboard request. (TaskStatusPersistenceService
    // writes the snapshot back periodically and on shutdown.)
    var taskStatusLogger = app.Services.GetRequiredService<ILogger<Program>>();
    SoftMedia.Server.Services.Infrastructure.TaskStatusStore.Load(
        registry, SoftMedia.Server.Services.Infrastructure.TaskStatusStore.DefaultPath(), taskStatusLogger);
}

using (var scope = app.Services.CreateScope())
{
    await DbInitializer.InitializeAsync(scope.ServiceProvider);
}

app.Run();

// Expose the implicit Program class so test projects can target it with
// `WebApplicationFactory<Program>`. Required because top-level statements
// generate an internal class by default.
public partial class Program { }
