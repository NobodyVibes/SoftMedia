using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using SoftMedia.Server.Data;
using SoftMedia.Server.Extensions;
using SoftMedia.Server.Hubs;
using SoftMedia.Server.Models.Options;
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
builder.Services.AddMediaServices();
builder.Services.AddBackgroundServices();
builder.Services.AddScoped<SoftMedia.Server.Services.Abstractions.IStreamSecurityService, SoftMedia.Server.Services.Security.StreamSecurityService>();
builder.Services.AddScoped<SoftMedia.Server.Services.Abstractions.ILibraryService, SoftMedia.Server.Services.Media.LibraryService>();
builder.Services.AddScoped<SoftMedia.Server.Services.Abstractions.IMediaRepository, SoftMedia.Server.Services.Infrastructure.MediaRepository>();
builder.Services.AddScoped<SoftMedia.Server.Services.Abstractions.ILibraryRepository, SoftMedia.Server.Services.Infrastructure.LibraryRepository>();
builder.Services.AddScoped<SoftMedia.Server.Services.Abstractions.IUserMediaInteractionRepository, SoftMedia.Server.Services.Infrastructure.UserMediaInteractionRepository>();
builder.Services.AddScoped<SoftMedia.Server.Services.Abstractions.IMediaService, SoftMedia.Server.Services.Media.MediaService>();
builder.Services.AddScoped<SoftMedia.Server.Services.Transcoding.ITranscodeDebugService, SoftMedia.Server.Services.Transcoding.TranscodeDebugService>();
builder.Services.AddScoped<SoftMedia.Server.Services.Media.IVideoPreviewService, SoftMedia.Server.Services.Media.VideoPreviewService>();



// SignalR for real-time updates
builder.Services.AddSignalR();

// API Configuration
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowFrontend", policy =>
    {
        var allowAnyOriginForLAN = builder.Configuration.GetValue<bool>("Cors:AllowAnyOriginForLAN");
        if (allowAnyOriginForLAN)
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
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseCors("AllowFrontend");
app.UseRateLimiter();
app.UseResponseCompression();

app.UseAuthentication();
app.UseAuthorization();

app.UseStaticFiles();

app.MapControllers();
app.MapHub<MediaHub>("/hubs/media");

using (var scope = app.Services.CreateScope())
{
    await DbInitializer.InitializeAsync(scope.ServiceProvider);
}

app.Run();

// Expose the implicit Program class so test projects can target it with
// `WebApplicationFactory<Program>`. Required because top-level statements
// generate an internal class by default.
public partial class Program { }
