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

// NR-WI-011 — runtime log level: one instance serves as BOTH a configuration source
// (the logging system reloads on its change token) and the DI-resolvable switch the
// settings UI applies through. It only contributes Logging:LogLevel:Default, so the
// more-specific category pins in appsettings (incl. the T6.6 token-safety pin on
// Hosting.Diagnostics) always outrank it.
var runtimeLogLevel = new SoftMedia.Server.Services.Infrastructure.RuntimeLogLevelProvider();
((IConfigurationBuilder)builder.Configuration).Add(runtimeLogLevel);
builder.Services.AddSingleton<SoftMedia.Server.Services.Infrastructure.IRuntimeLogLevel>(runtimeLogLevel);

// NR-WI-011 — capped in-memory log capture for the admin log viewer.
var logRingBuffer = new SoftMedia.Server.Services.Infrastructure.LogRingBuffer();
builder.Services.AddSingleton(logRingBuffer);
builder.Logging.AddProvider(new SoftMedia.Server.Services.Infrastructure.RingBufferLoggerProvider(logRingBuffer));

// SR-WI-064 — persistent log sink: daily rolling files under {ContentRoot}/data/logs
// (content root, NOT the CWD, so a service-host launch from elsewhere still lands in
// the app's data directory), 7-day retention, Warning+ by default. The provider-specific
// filter rule keeps its floor independent of the runtime-adjustable Default level (a
// provider-scoped rule outranks global rules for this provider), and the provider itself
// enforces the same floor internally plus the T6.6 ?token= scrub — see RollingFileLogger.cs.
var fileLogDirectory = Path.Combine(builder.Environment.ContentRootPath, "data", "logs");
var fileLogLevel = Enum.TryParse<LogLevel>(
        builder.Configuration["FileLogging:MinimumLevel"], ignoreCase: true, out var configuredFileLevel)
    ? configuredFileLevel
    : LogLevel.Warning;
var fileLogRetentionDays = builder.Configuration.GetValue<int?>("FileLogging:RetentionDays") ?? 7;
builder.Logging.AddProvider(new SoftMedia.Server.Services.Infrastructure.RollingFileLoggerProvider(
    fileLogDirectory, fileLogLevel, fileLogRetentionDays));
builder.Logging.AddFilter<SoftMedia.Server.Services.Infrastructure.RollingFileLoggerProvider>(null, fileLogLevel);
builder.Services.AddSingleton(new SoftMedia.Server.Services.Infrastructure.FileLogSinkInfo(fileLogDirectory));

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
    options.UseSqlite(builder.Configuration.GetConnectionString("DefaultConnection"))
        // SR-WI-035: WAL/busy_timeout/synchronous asserted on every open, not left to
        // whatever mode the DB file happens to carry.
        .AddInterceptors(new SqlitePragmaInterceptor()));

// Register Application Services via Extensions
builder.Services.AddIdentityServices(builder.Configuration);
builder.Services.AddSecurityServices();
builder.Services.AddMediaServices();
builder.Services.AddBackgroundServices();



// SignalR for real-time updates. The hub JSON protocol serializes with its OWN
// options (not the MVC AddJsonOptions below), so the SR-WI-060 UTC converter is
// registered here too — today's hub payloads are strings/ints, but any future
// DateTime in a hub payload must carry the same explicit-UTC contract as the API.
builder.Services.AddSignalR()
    .AddJsonProtocol(options =>
    {
        options.PayloadSerializerOptions.Converters.Add(new SoftMedia.Server.Helpers.UtcDateTimeJsonConverter());
    });

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
        // SR-WI-060: SQLite round-trips stored-UTC DateTimes as Kind=Unspecified, which
        // serialized without a "Z" and was parsed as LOCAL time by JS clients. This
        // converter stamps Unspecified as UTC on write (and parses tolerantly on read),
        // so every DateTime the API emits carries an explicit UTC marker. DateTime? is
        // covered by the framework's nullable wrapper around the same converter.
        options.JsonSerializerOptions.Converters.Add(new SoftMedia.Server.Helpers.UtcDateTimeJsonConverter());
    });

// SR-WI-061 — RFC 7807 everywhere. AddProblemDetails backs the parameterless
// UseExceptionHandler below (unhandled exceptions become application/problem+json
// instead of empty-body 500s) and stamps a traceId extension on every ProblemDetails
// the framework writes — MVC's DefaultProblemDetailsFactory ([ApiController] validation
// responses and controller Problem() helpers) honors CustomizeProblemDetails too.
// No stack traces are ever included: the handler only emits status/title/traceId.
builder.Services.AddProblemDetails(options =>
{
    options.CustomizeProblemDetails = ctx =>
    {
        ctx.ProblemDetails.Extensions.TryAdd(
            "traceId",
            System.Diagnostics.Activity.Current?.Id ?? ctx.HttpContext.TraceIdentifier);
    };
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

// SR-WI-061: outermost exception boundary. With AddProblemDetails registered, the
// parameterless UseExceptionHandler writes RFC 7807 (500 + title + traceId, never a
// stack trace) for any unhandled exception in the pipeline — in EVERY environment,
// so Production no longer returns empty-body 500s and tests see the same shape.
app.UseExceptionHandler();

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
            // SR-WI-061: same RFC 7807 envelope as every other error. The machine-read
            // discriminator lives on as the "error" extension so existing consumers
            // keying on password_change_required keep working.
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            var problem = new Microsoft.AspNetCore.Mvc.ProblemDetails
            {
                Status = StatusCodes.Status403Forbidden,
                Title = "Password change required",
                Detail = "You must change your password before continuing.",
            };
            problem.Extensions["error"] = "password_change_required";
            problem.Extensions["traceId"] =
                System.Diagnostics.Activity.Current?.Id ?? context.TraceIdentifier;
            await context.Response.WriteAsJsonAsync(
                problem, options: null, contentType: "application/problem+json");
            return;
        }
    }
    await next();
});

app.UseStaticFiles();

app.MapControllers();
app.MapHub<MediaHub>("/hubs/media");

// SR-WI-061 verification hook: a deliberately-unhandled exception route so the
// global handler's RFC 7807 contract stays integration-testable. Never mapped in
// Production (Development + the test harness's "Testing" environment only).
if (!app.Environment.IsProduction())
{
    app.MapGet("/api/v1/debug/throw",
        string () => throw new InvalidOperationException(
            "Deliberate unhandled exception (non-production SR-WI-061 test endpoint)."));
}

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

    // NR-WI-011: apply the persisted log level now that the DB (and its seeded
    // defaults) exist. Later changes apply through SettingsController.
    var settingsService = scope.ServiceProvider
        .GetRequiredService<SoftMedia.Server.Services.Infrastructure.ISettingsService>();
    runtimeLogLevel.Apply(await settingsService.GetSettingAsync("LogLevel", "Information"));
}

app.Run();

// Expose the implicit Program class so test projects can target it with
// `WebApplicationFactory<Program>`. Required because top-level statements
// generate an internal class by default.
public partial class Program { }
